using Dapper;
using Lugiano.Workflow.SyncService.ChiroTouch;
using Lugiano.Workflow.SyncService.Services.Pdf;
using Lugiano.Workflow.SyncService.Util;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lugiano.Workflow.SyncService.Services;

// Phase 1 HCFA-1500 (CMS-1500) preview generator. Reads canonical claim data
// from PSChiro for a single (patient, appointment) and renders a PDF that
// mirrors the form's box structure. Read-only — does NOT write the
// "claim generated/printed" state back to PSChiro tables. That's Phase 2.
//
// Defaults applied (revisit when Adam/Jacob confirm):
//   - Box 24B place of service: "11" (office)
//   - Box 24E diagnosis pointer: "1" (first DX on the claim)
//   - Box 25 federal tax ID: blank — no canonical source yet on Practice/Doctor
//   - Box 24 units: 1 per line
//   - Box 21 ICD indicator: "0" (ICD-10) when InsPolicies.ICDMode is null
public sealed class HcfaPreviewService
{
    private readonly ISourceDbConnectionFactory _sourceDb;
    private readonly IChartNoteReadQueries _noteReads;
    private readonly ILogger<HcfaPreviewService> _logger;

    public HcfaPreviewService(
        ISourceDbConnectionFactory sourceDb,
        IChartNoteReadQueries noteReads,
        ILogger<HcfaPreviewService> logger)
    {
        _sourceDb = sourceDb;
        _noteReads = noteReads;
        _logger = logger;
    }

    public async Task<HcfaData?> GetDataAsync(int patientId, int appointmentId, CancellationToken ct = default)
    {
        await using var conn = _sourceDb.Create();

        // 1. Patient demographics + insurance + appointment + doctor in one round-trip.
        const string headerSql =
            """
            SELECT
              p.ID                 AS PatientId,
              p.AccountNo          AS AccountNo,
              p.FirstName          AS PatientFirstName,
              p.MiddleName         AS PatientMiddleName,
              p.LastName           AS PatientLastName,
              p.Sex                AS PatientSex,
              p.BirthDate          AS PatientBirthDate,
              p.Address            AS PatientAddress,
              p.City               AS PatientCity,
              p.State              AS PatientState,
              p.Zip                AS PatientZip,
              (SELECT TOP 1 ci.Number
                 FROM dbo.ContactInfos ci
                 WHERE ci.PatientID = p.ID
                 ORDER BY ci.ID) AS PatientPhone,
              p.CurInjuryDate      AS InjuryDate,
              p.AutoState          AS AutoState,
              ip.InsCoName         AS InsCoName,
              ip.CompanyAddress    AS InsCoAddress,
              ip.CompanyCity       AS InsCoCity,
              ip.CompanyState      AS InsCoState,
              ip.CompanyZip        AS InsCoZip,
              ip.InsuredName       AS InsuredName,
              ip.InsuredIDNo       AS InsuredIdNo,
              ip.Relationship      AS Relationship,
              ip.AcceptAssignment  AS AcceptAssignment,
              ip.PolGrpFECANum     AS GroupNo,
              ip.PriorAuthNo       AS PriorAuthNo,
              ip.Box33NPI          AS BillingNpi,
              -- Rendering-provider NPI lookup chain for Box 24J:
              --   1. InsPolicies.Box24JNPI (per-policy override) — highest precedence
              --   2. InsCoDoctorXref.Box24JNPI for (this carrier, this doctor)
              --   3. Doctors.NPI — fallback
              -- Empty overrides naturally fall through to the doctor's main NPI.
              COALESCE(NULLIF(ip.Box24JNPI, ''),
                       (SELECT NULLIF(xref.Box24JNPI, '')
                          FROM dbo.InsCoDoctorXref xref
                          WHERE xref.DoctorID = d.ID
                            AND xref.InsCoID = ip.SourceInsCoID),
                       d.NPI)            AS RenderingNpi,
              ip.BillProvName      AS BillProvName,
              ip.BillProvPIN       AS BillProvPin,
              ip.BillProvGRP       AS BillProvGrp,
              ip.ICDMode           AS IcdMode,
              a.ID                 AS AppointmentId,
              a.ScheduleDateTime   AS AppointmentDateTime,
              d.ID                 AS DoctorId,
              d.FullName           AS DoctorFullName,
              d.NPI                AS DoctorNpi,
              d.FacilityNPI        AS FacilityNpi,
              d.FacilityAddress    AS FacilityAddress,
              d.FacilityPhone      AS FacilityPhone,
              -- Practice-level billing config from the most recent
              -- ClaimBillingProviders row (same EIN/NPI/address across all
              -- doctors at this practice — it's the practice's identity).
              (SELECT TOP 1 cbp.LastName FROM dbo.ClaimBillingProviders cbp
                 WHERE cbp.Identifier1Code = 'XX' ORDER BY cbp.ID DESC) AS BillEntityName,
              (SELECT TOP 1 cbp.Identifier2 FROM dbo.ClaimBillingProviders cbp
                 WHERE cbp.Identifier2Code = 'EI' ORDER BY cbp.ID DESC) AS BillEntityEin,
              (SELECT TOP 1 cbp.PayToAddress1 FROM dbo.ClaimBillingProviders cbp
                 WHERE cbp.Identifier2Code = 'EI' ORDER BY cbp.ID DESC) AS PayToAddress1,
              (SELECT TOP 1 cbp.PayToCity FROM dbo.ClaimBillingProviders cbp
                 WHERE cbp.Identifier2Code = 'EI' ORDER BY cbp.ID DESC) AS PayToCity,
              (SELECT TOP 1 cbp.PayToState FROM dbo.ClaimBillingProviders cbp
                 WHERE cbp.Identifier2Code = 'EI' ORDER BY cbp.ID DESC) AS PayToState,
              (SELECT TOP 1 cbp.PayToZipCode FROM dbo.ClaimBillingProviders cbp
                 WHERE cbp.Identifier2Code = 'EI' ORDER BY cbp.ID DESC) AS PayToZip
            FROM       dbo.Appointments a
            JOIN       dbo.Patients     p  ON p.ID = a.PatientID
            JOIN       dbo.Doctors      d  ON d.ID = a.DoctorID
            LEFT JOIN  dbo.InsPolicies  ip ON ip.PatientID = p.ID AND ip.Seq = 1
            WHERE      a.ID        = @appointmentId
              AND      a.PatientID = @patientId;
            """;

        var header = await conn.QueryFirstOrDefaultAsync<HcfaHeaderRow>(
            headerSql, new { patientId, appointmentId });
        if (header is null) return null;

        // 2. Diagnoses for this appointment, in seq order (A..L on the form).
        var dxRows = (await conn.QueryAsync<HcfaDxRow>(
            """
            SELECT TOP 12 Code, Description, Seq
            FROM   dbo.Diagnoses
            WHERE  AppointmentID = @appointmentId
            ORDER BY Seq;
            """,
            new { appointmentId })).ToList();

        // 3. Service lines for this appointment (CPT charges only).
        //    DiagnosisPointer/Modifiers don't live on Transactions for unbilled
        //    charges — they're populated on ClaimLines at claim-generation time.
        //    Default pointer to "1" + blank modifiers for the preview.
        var lineRows = (await conn.QueryAsync<HcfaLineRow>(
            """
            SELECT ID         AS TranId,
                   TranDate   AS DateOfService,
                   Code       AS Cpt,
                   Description,
                   TranAmt    AS Charge
            FROM   dbo.Transactions
            WHERE  ApptID      = @appointmentId
              AND  TranType    = 'C'
              AND  TranSubType = 'SV'
            ORDER BY ID;
            """,
            new { appointmentId })).ToList();

        if (lineRows.Count == 0)
        {
            _logger.LogWarning(
                "HCFA preview: appointment {ApptId} for patient {PatientId} has no service charges; nothing to bill.",
                appointmentId, patientId);
            return null;
        }

        // 4. Chart note(s) for this appointment's DOS. ChartNotes has no
        //    AppointmentID, so match by (PatientID + same calendar date).
        //    Usually one note per visit; multi-provider visits can have more.
        var noteHeaders = (await conn.QueryAsync<ChartNoteHeader>(
            """
            SELECT cn.ID AS NoteId, cn.NoteDate, cn.SOAPPtr AS TextPtr,
                   d.FullName AS DoctorName, cn.DoctorID AS DoctorId
            FROM   dbo.ChartNotes cn
            LEFT JOIN dbo.Doctors d ON d.ID = cn.DoctorID
            WHERE  cn.PatientID = @patientId
              AND  CAST(cn.NoteDate AS date) = CAST(@dos AS date)
            ORDER BY cn.ID;
            """,
            new { patientId, dos = header.AppointmentDateTime ?? DateTime.Today })).ToList();

        // Signature lookup: prefer the row signed against THIS specific
        // ChartNote (SigType='CN' + SigTypeID=noteId — CT's polymorphic key),
        // fall back to the doctor's most recent signature image so portal-
        // generated notes (which may not have a per-note signature row yet)
        // still render with the doctor's mark.
        var noteIds = noteHeaders.Select(n => n.NoteId).ToArray();
        var perNoteSigs = noteIds.Length == 0
            ? new Dictionary<int, SigRow>()
            : (await conn.QueryAsync<SigRow>(
                """
                SELECT SigTypeID AS NoteId, ImageBase64 AS Image, SigTimestamp AS SignedAt
                FROM   dbo.Signatures
                WHERE  SigType = 'CN'
                  AND  SigTypeID IN @noteIds
                  AND  ImageBase64 IS NOT NULL;
                """,
                new { noteIds })).ToDictionary(r => r.NoteId);

        var notes = new List<HcfaNote>();
        foreach (var nh in noteHeaders)
        {
            string? text = null;
            IReadOnlyList<IReadOnlyList<RtfRun>>? rich = null;
            if (nh.TextPtr is int ptr and not 0)
            {
                try
                {
                    var rtf = await _noteReads.GetNoteRtfAsync(ptr);
                    text = RtfConverter.ToPlainText(rtf);
                    rich = RtfRichConverter.ToRuns(rtf);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "HCFA packet: failed to read RTF for ptr {Ptr} (note {NoteId}); rendering placeholder.",
                        ptr, nh.NoteId);
                }
            }
            // Per-note signature first; fall back to whatever this doctor has
            // signed most recently. Avoids a blank signature line when CT's
            // CN row exists but ImageBase64 was never populated.
            string? sigImage = null;
            DateTime? signedAt = null;
            if (perNoteSigs.TryGetValue(nh.NoteId, out var sig))
            {
                sigImage = sig.Image;
                signedAt = sig.SignedAt;
            }
            if (sigImage is null && nh.DoctorId is int did)
            {
                var fallback = await conn.QuerySingleOrDefaultAsync<SigRow>(
                    """
                    SELECT TOP 1 ImageBase64 AS Image, SigTimestamp AS SignedAt
                    FROM   dbo.Signatures
                    WHERE  SigType = 'CN'
                      AND  DoctorID = @did
                      AND  ImageBase64 IS NOT NULL
                    ORDER BY SigTimestamp DESC;
                    """,
                    new { did });
                sigImage = fallback?.Image;
                signedAt ??= fallback?.SignedAt;
            }
            notes.Add(new HcfaNote(nh.NoteId, nh.NoteDate, nh.DoctorName, text, sigImage, signedAt, rich));
        }

        return new HcfaData(header, dxRows, lineRows, notes);
    }

    public byte[] RenderPdf(HcfaData data, bool calibrate = false, float dx = 0, float dy = 0, bool fax = false)
    {
        // QuestPDF requires a license declaration at runtime. Community license
        // covers orgs under $1M revenue — fits Lugiano. Set once is fine.
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(doc => AddPagesToDocument(doc, data, calibrate, dx, dy, fax)).GeneratePdf();
    }

    // Adds this HCFA's pages (form + chart notes) onto an existing document
    // container. Used by the tracer endpoint to interleave HCFA forms after
    // each tracer batch page — see TracerController.Preview. Caller owns the
    // Document.Create wrapper and QuestPDF.Settings.License set-up.
    public void AddPagesToDocument(IDocumentContainer doc, HcfaData data,
        bool calibrate = false, float dx = 0, float dy = 0, bool fax = false)
    {
        // Print-mode rendering with ABSOLUTE positioning. Coordinates were
        // measured from a real filled CMS-1500 (Good2Go claim 2024-58949,
        // patient Ortiz Zapata, faxed 12/30/25) via pdftotext -bbox-layout.
        // Numbers are PDF points (1pt = 1/72") from the top-left of an
        // 8.5×11 letter page. Each field's (x, y) corresponds to the start
        // of the text glyph in the matching pre-printed box on red paper.
        //
        // calibrate=true overlays tiny markers + box labels at each field
        // position so the user can print on plain paper, hold against a real
        // form, and verify alignment. Use ?calibrate=true on the endpoint.
        //
        // fax=true composites a color CMS-1500 (02-12) form image as the
        // page background, so the carrier receives a complete-looking form
        // via fax. The data coordinates are the same in both modes — CT uses
        // one alignment for mail + fax, and we match that. The overlay PNG is
        // sized to the full letter page; if the boxes don't line up under the
        // data the PNG needs re-rasterizing, not separate coordinates.
        var fields = BuildFieldList(data);
        var overlayPath = fax ? ResolveOverlayPath() : null;

        // ---- PAGE 1: the HCFA form ----
        doc.Page(page =>
            {
                page.Size(PageSizes.Letter);
                page.Margin(0);
                page.DefaultTextStyle(t => t.FontSize(9).FontFamily("Courier New"));

                page.Content().Layers(layers =>
                {
                    // Primary layer is empty (transparent) — we just need a
                    // canvas for the absolutely-positioned children.
                    layers.PrimaryLayer().Width(612).Height(792);

                    // Fax overlay: blank CMS-1500 form rendered underneath
                    // the data so the recipient sees a complete form. Added
                    // BEFORE the field layers so text sits on top of the
                    // form lines (matching CT's fax output).
                    if (overlayPath is not null)
                    {
                        layers.Layer().AlignLeft().AlignTop()
                            .Width(612).Height(792)
                            .Image(overlayPath).FitArea();
                    }

                    foreach (var f in fields)
                    {
                        // Apply per-printer global offsets (dx, dy in PDF points).
                        // Standard pattern: every CMS-1500 product uses two
                        // calibration numbers per printer to compensate for
                        // margin + scaling differences. Dial once, persist later.
                        var x = Math.Max(0, f.X + dx);
                        var y = Math.Max(0, f.Y + dy);

                        layers.Layer().AlignLeft().AlignTop()
                            .PaddingLeft(x).PaddingTop(y)
                            .Text(f.Value).FontSize(f.FontSize).FontFamily("Courier New");

                        if (calibrate)
                        {
                            // Tiny grey label one line above the value showing
                            // the box id — only renders in ?calibrate=true mode.
                            layers.Layer().AlignLeft().AlignTop()
                                .PaddingLeft(x).PaddingTop(Math.Max(0, y - 6))
                                .Text(f.BoxId).FontSize(5).FontColor(Colors.Grey.Medium);
                        }
                    }
                });
            });

            // ---- PAGE 2+: chart notes for this DOS ----
            // Delegated to the shared renderer so the printed/faxed chart-note
            // format + signature block are identical across every flow and match
            // ChiroTouch's output. See ChartNotesRenderer.
            var ctx = new ChartNoteHeaderCtx(
                PatientDisplayName: data.Header.PatientDisplayName,
                AccountNo: data.Header.AccountNo?.ToString() ?? data.Header.PatientId.ToString(),
                PatientBirthDate: data.Header.PatientBirthDate,
                InsCoName: data.Header.InsCoName,
                PolicyNo: data.Header.GroupNo,
                InsuredId: data.Header.InsuredIdNo,
                FacilityBlock: data.Header.FacilityAddress);

            ChartNotesRenderer.AddNotePages(doc, ctx, data.Notes.Select(n => new RenderableNote(
                NoteDate: n.NoteDate,
                ProviderName: n.DoctorName,
                RichBody: n.RichBody,
                PlainTextFallback: n.PlainText,
                SignatureImageBase64: n.SignatureImage,
                SignedProviderName: n.DoctorName,
                SignedAt: n.SignedAt)));
    }

    // Build the absolutely-positioned field list for one claim. Coordinates
    // are PDF points from page top-left, derived from the Good2Go reference
    // claim that we know prints correctly on real CMS-1500 paper. Tweak per
    // printer via the ?calibrate=true overlay.
    private static List<HcfaField> BuildFieldList(HcfaData data)
    {
        var f = new List<HcfaField>();
        var h = data.Header;
        var rel = (h.Relationship ?? "Self").Trim();
        var isSelf = rel.Equals("Self", StringComparison.OrdinalIgnoreCase);
        var sex = (h.PatientSex ?? string.Empty).Trim();
        var sexInitial = sex.Length > 0 ? char.ToUpperInvariant(sex[0]) : ' ';

        // Carrier mailing address — top-right block (~x=311, y=26-49)
        if (!string.IsNullOrWhiteSpace(h.InsCoName))
            f.Add(new("Carrier", 311, 26, h.InsCoName!));
        if (!string.IsNullOrWhiteSpace(h.InsCoAddress))
            f.Add(new("Carrier addr", 311, 38, h.InsCoAddress!));
        var csz = JoinNonEmpty(", ", h.InsCoCity, JoinNonEmpty(" ", h.InsCoState, h.InsCoZip));
        if (!string.IsNullOrWhiteSpace(csz))
            f.Add(new("Carrier csz", 311, 49, csz));

        // Box 1 — Insurance type ("OTHER" column X for Auto/PIP)
        f.Add(new("1-OTHER", 340.8f, 107, "X"));

        // Box 1a — Insured's ID#
        if (!string.IsNullOrWhiteSpace(h.InsuredIdNo))
            f.Add(new("1a", 383, 109, h.InsuredIdNo!));

        // Box 2 — Patient name "Last, First"
        f.Add(new("2", 27.5f, 132, h.PatientDisplayName));

        // Box 3 — Patient DOB MM/DD/YYYY + Sex
        if (h.PatientBirthDate is DateTime dob)
        {
            f.Add(new("3-MM", 241, 130, dob.ToString("MM")));
            f.Add(new("3-DD", 259, 130, dob.ToString("dd")));
            f.Add(new("3-YYYY", 277, 130, dob.ToString("yyyy")));
        }
        // Box 3 Sex — CT puts X at 319.6 for one column, the other is to its
        // left at ~301. Verify against CT's blank-form which column is M vs F.
        if (sexInitial == 'M') f.Add(new("3-M", 301f, 130, "X"));
        else if (sexInitial == 'F') f.Add(new("3-F", 319.6f, 130, "X"));

        // Box 4 — Insured's name
        f.Add(new("4", 383, 132, h.InsuredName ?? h.PatientDisplayName));

        // Box 5 — Patient address + phone. Phone is split into area code (3
        // digits at x=126.5) and 7-digit number (at x=150.5), no separators,
        // matching ChiroTouch's format.
        if (!string.IsNullOrWhiteSpace(h.PatientAddress)) f.Add(new("5-street", 27.5f, 157, h.PatientAddress!));
        if (!string.IsNullOrWhiteSpace(h.PatientCity))    f.Add(new("5-city",   27.5f, 180, h.PatientCity!));
        if (!string.IsNullOrWhiteSpace(h.PatientState))   f.Add(new("5-state", 205,    180, h.PatientState!));
        if (!string.IsNullOrWhiteSpace(h.PatientZip))     f.Add(new("5-zip",    27.5f, 206, h.PatientZip!));
        var (pArea, pNum) = SplitPhone(h.PatientPhone);
        if (pArea is not null) f.Add(new("5-phone-area", 126.5f, 206, pArea));
        if (pNum  is not null) f.Add(new("5-phone-num",  150.5f, 206, pNum));

        // Box 6 — Relationship to insured (Self / Spouse / Child / Other)
        var (relX, relLabel) = rel.ToLowerInvariant() switch
        {
            "self"   => (254.8f, "6-Self"),
            "spouse" => (297f,   "6-Spouse"),
            "child"  => (340f,   "6-Child"),
            _        => (383f,   "6-Other"),
        };
        f.Add(new(relLabel, relX, 155, "X"));

        // Box 7 — Insured's address (same as patient when Self)
        if (isSelf)
        {
            if (!string.IsNullOrWhiteSpace(h.PatientAddress)) f.Add(new("7-street", 383, 157, h.PatientAddress!));
            if (!string.IsNullOrWhiteSpace(h.PatientCity))    f.Add(new("7-city",   383, 180, h.PatientCity!));
            if (!string.IsNullOrWhiteSpace(h.PatientState))   f.Add(new("7-state", 549, 180, h.PatientState!));
            if (!string.IsNullOrWhiteSpace(h.PatientZip))     f.Add(new("7-zip",    383, 206, h.PatientZip!));
            if (pArea is not null) f.Add(new("7-phone-area", 486.5f, 206, pArea));
            if (pNum  is not null) f.Add(new("7-phone-num",  510.5f, 206, pNum));
        }

        // Box 10 — Condition related to (defaults: Employment N, Other N).
        // Auto Accident YES/NO based on injury date. YES col ~x=269, NO col ~x=313.
        f.Add(new("10a-N", 313.2f, 252, "X"));                       // Employment? No
        f.Add(new("10b",   h.IsAutoCase ? 269.5f : 313.2f, 278, "X")); // Auto?
        f.Add(new("10c-N", 313.2f, 300, "X"));                       // Other? No
        if (h.IsAutoCase)
        {
            var state = h.AutoState ?? h.PatientState;
            if (!string.IsNullOrWhiteSpace(state))
                f.Add(new("10b-state", 335, 278, state!));
        }

        // Box 11 — Insured's Policy/Group/FECA #. For Auto, often the claim #
        // landed here; we don't have a canonical source yet so fall back to
        // group number or insured ID.
        var box11 = h.GroupNo ?? h.InsuredIdNo;
        if (!string.IsNullOrWhiteSpace(box11))
            f.Add(new("11", 383, 228, box11!));

        // Box 11a — Insured DOB + sex (same as patient when Self)
        if (isSelf && h.PatientBirthDate is DateTime idob)
        {
            f.Add(new("11a-MM", 403, 253, idob.ToString("MM")));
            f.Add(new("11a-DD", 421, 253, idob.ToString("dd")));
            f.Add(new("11a-YYYY", 439, 253, idob.ToString("yyyy")));
            // Box 11a Sex — CT puts X at 504.5
            if (sexInitial == 'M') f.Add(new("11a-M", 486f, 253, "X"));
            else if (sexInitial == 'F') f.Add(new("11a-F", 504.5f, 253, "X"));
        }

        // Box 11d — Is there another health benefit plan? Default N.
        f.Add(new("11d-N", 425.8f, 325, "X"));

        // Box 12 — Patient signature + date (date = today for the demo)
        f.Add(new("12-sig", 77, 370, "Signature On File"));
        f.Add(new("12-MM", 34.2f, 397, DateTime.Today.ToString("MM")));
        f.Add(new("12-DD", 52.2f, 397, DateTime.Today.ToString("dd")));
        f.Add(new("12-YYYY", 70.2f, 397, DateTime.Today.ToString("yyyy")));

        // Box 13 — Insured signature
        f.Add(new("13-sig", 428, 370, "Signature On File"));

        // Box 14 — Date of current illness/injury
        if (h.InjuryDate is DateTime inj)
        {
            f.Add(new("14-MM", 279.5f, 370, inj.ToString("MM")));
            f.Add(new("14-DD", 297.5f, 370, inj.ToString("dd")));
            f.Add(new("14-YYYY", 315.5f, 370, inj.ToString("yyyy")));
        }

        // Box 20 — Outside lab? Default N.
        f.Add(new("20-N", 430.6f, 446, "X"));

        // Box 21 — ICD indicator ("0" = ICD-10, "9" = ICD-9)
        f.Add(new("21-ind", 322.2f, 460, data.IcdIndicator));

        // Box 21 — Diagnosis codes A..L (4 columns × 3 rows). CT strips the
        // decimal from ICD-10 codes ("M54.31" → "M5431") because the form's
        // narrow column doesn't fit the dot. Row spacing is ~11.3pt per CT.
        var dxX = new[] { 36.5f, 132f, 227.8f, 323.4f };
        var dxY = new[] { 470.5f, 481.8f, 493.1f };
        for (int i = 0; i < Math.Min(12, data.Diagnoses.Count); i++)
        {
            var raw = data.Diagnoses[i].Code;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var code = raw!.Replace(".", string.Empty);
            var letter = ((char)('A' + i)).ToString();
            f.Add(new($"21-{letter}", dxX[i % 4], dxY[i / 4], code));
        }

        // Box 23 — Prior authorization
        if (!string.IsNullOrWhiteSpace(h.PriorAuthNo))
            f.Add(new("23", 380, 460, h.PriorAuthNo!));

        // Box 24 — Service lines (up to 6 rows starting at y=543, 24pt spacing
        // per CT). Each form row actually has a supplemental-info sub-row
        // above it (y=531.6 for row 1, etc.) for NDC/anesthesia data — we
        // don't render those today; chiro claims don't use them.
        var rowY = 543f;
        const float rowSpacing = 24f;
        for (int li = 0; li < Math.Min(6, data.Lines.Count); li++)
        {
            var line = data.Lines[li];
            var dos = line.DateOfService ?? DateTime.Today;
            f.Add(new($"24A-from-MM-{li}",   25.2f,  rowY, dos.ToString("MM")));
            f.Add(new($"24A-from-DD-{li}",   43.2f,  rowY, dos.ToString("dd")));
            f.Add(new($"24A-from-YYYY-{li}", 61.2f,  rowY, dos.ToString("yyyy")));
            f.Add(new($"24A-to-MM-{li}",     88.2f,  rowY, dos.ToString("MM")));
            f.Add(new($"24A-to-DD-{li}",    106.2f,  rowY, dos.ToString("dd")));
            f.Add(new($"24A-to-YYYY-{li}",  124.2f,  rowY, dos.ToString("yyyy")));
            f.Add(new($"24B-POS-{li}",      151.2f,  rowY, "11"));
            f.Add(new($"24D-CPT-{li}",      197.3f,  rowY, line.Cpt ?? string.Empty));
            // Modifier rule: 97150 (Exercise Group) always carries GP per
            // historical claim patterns (6,794 confirmations). Extend the
            // rule table when other CPT/modifier rules are confirmed.
            if (string.Equals(line.Cpt, "97150", StringComparison.OrdinalIgnoreCase))
                f.Add(new($"24D-Mod-{li}",  254.8f,  rowY, "GP"));
            // DX pointer default: 1234 (renders as "ABCD" on the form),
            // matching the standard pattern in ClaimLines. Per-CPT override
            // logic can refine this when CT's exact rules are confirmed.
            f.Add(new($"24E-DX-{li}",       338f,    rowY, "ABCD"));
            // Charge X position matches CT (411.2). Keep dollars-with-decimal
            // ("45.00") which is the readable format both formats accept.
            f.Add(new($"24F-chg-{li}",      411.2f,  rowY, line.Charge.ToString("0.00")));
            f.Add(new($"24G-units-{li}",    446f,    rowY, "1"));
            f.Add(new($"24H-EPSDT-{li}",    466.2f,  rowY, "N"));
            // Box 24J — use the chained-lookup RenderingNpi (per-policy override
            // → per-(carrier,doctor) override → Doctors.NPI fallback) so per-
            // carrier billing NPIs land correctly when configured.
            var rendNpi = h.RenderingNpi ?? h.DoctorNpi;
            if (!string.IsNullOrWhiteSpace(rendNpi))
                f.Add(new($"24J-NPI-{li}",  504.5f,  rowY, rendNpi!));

            // Shaded supplemental sub-row (12pt above the main row) carries
            // the rendering provider's qualifier + taxonomy code:
            //   "ZZ" = qualifier indicating a Health Care Provider Taxonomy
            //   "111N00000X" = NUCC taxonomy code for Chiropractor (universal
            //                  for chiro practices — Lugiano is 100% chiro)
            // CT shows this strip at y=531.6 for line 1 (=543-11.4). Per-
            // doctor taxonomy would override this when not chiro.
            f.Add(new($"24I-qual-{li}",     486.5f, rowY - 12, "ZZ"));
            f.Add(new($"24J-taxonomy-{li}", 504.5f, rowY - 12, "111N00000X"));
            rowY += rowSpacing;
        }

        // Box 25 — Federal Tax ID + EIN/SSN checkbox. EIN comes from
        // ClaimBillingProviders.Identifier2 (Identifier2Code='EI'). When
        // present, also mark the EIN checkbox X. Practice-level config.
        if (!string.IsNullOrWhiteSpace(h.BillEntityEin))
        {
            f.Add(new("25-EIN", 24f, 683, h.BillEntityEin!));
            f.Add(new("25-EIN-X", 153f, 683, "X")); // EIN column (right) checkbox
        }

        // Box 26 — Patient account #. Uses Patients.AccountNo (CT's billing
        // account number, e.g. "306235"), NOT Patients.ID (our internal
        // PatientId). CT writes the account number; the patient ID is for
        // internal lookup only.
        var acct = h.AccountNo?.ToString() ?? h.PatientId.ToString();
        f.Add(new("26", 180, 683, acct));

        // Box 27 — Accept assignment. CT puts the X at (290.28, 683) in the
        // same row as Box 25/26 — NOT at y=707 (which collides with Box 32
        // facility line 1). Producing the "BA" overlap I was seeing.
        f.Add(new("27", h.AcceptAssignment == 1 ? 290.28f : 320f, 683, "X"));

        // Box 28/29/30 — CT writes these as CENTS with no decimal
        // ("30000" = $300.00, "000" = $0.00). The dollar-sign and decimal
        // come from the pre-printed form. Position y≈685.5, X=416.5/500.5.
        var totalCents = ((int)Math.Round(data.TotalCharge * 100)).ToString();
        f.Add(new("28", 416.5f, 685.5f, totalCents));
        f.Add(new("29", 500.5f, 685.5f, "000"));

        // Box 31 — Provider signature + date (date split into MM/DD/YYYY,
        // matching CT layout: MM at 126.7, DD at 143.3, YYYY at 159.8).
        if (!string.IsNullOrWhiteSpace(h.DoctorFullName))
            f.Add(new("31-name", 31.5f, 725.6f, h.DoctorFullName!));
        var sigDate = DateTime.Today;
        f.Add(new("31-date-MM",   126.7f, 746.6f, sigDate.ToString("MM")));
        f.Add(new("31-date-DD",   143.3f, 746.6f, sigDate.ToString("dd")));
        f.Add(new("31-date-YYYY", 159.8f, 746.6f, sigDate.ToString("yyyy")));

        // Box 32 — Service facility (rendered position). CT layout:
        //   Line 1 (name)    y=707.6  x=189
        //   Line 2 (street)  y=718    x=189
        //   Line 3 (csz)     y=728.5  x=189
        //   NPI              y=746.6  x=189
        // Splits FacilityAddress on \n or \r\n.
        var facLines = (h.FacilityAddress ?? string.Empty)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var facYs = new[] { 707.6f, 718f, 728.5f };
        for (int i = 0; i < Math.Min(3, facLines.Length); i++)
        {
            f.Add(new($"32-line-{i}", 189f, facYs[i], facLines[i].Trim()));
        }
        if (!string.IsNullOrWhiteSpace(h.FacilityNpi))
            f.Add(new("32a-NPI", 189f, 746.6f, h.FacilityNpi!));

        // Box 33 — Billing provider (the remit-to address — usually the
        // practice's PO box, distinct from the service facility). Sourced
        // from ClaimBillingProviders.LastName + PayToAddress* (practice-
        // wide config). Falls back to facility info if no billing config
        // is present (e.g. a clinic that hasn't generated claims yet).
        var billName = !string.IsNullOrWhiteSpace(h.BillEntityName) ? h.BillEntityName! : null;
        var payZip = string.IsNullOrWhiteSpace(h.PayToZip)
            ? null
            : (h.PayToZip!.Length == 9
                ? $"{h.PayToZip[..5]}-{h.PayToZip[5..]}"
                : h.PayToZip);
        var payCsz = JoinNonEmpty(", ", h.PayToCity,
            JoinNonEmpty(" ", h.PayToState, payZip));

        var billLines = billName is not null
            ? new[] { billName, h.PayToAddress1 ?? string.Empty, payCsz }
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray()
            : facLines; // fallback for clinics without ClaimBillingProviders config

        for (int i = 0; i < Math.Min(3, billLines.Length); i++)
        {
            f.Add(new($"33-line-{i}", 382.6f, facYs[i], billLines[i].Trim()));
        }
        var billNpi = h.BillingNpi ?? h.FacilityNpi;
        if (!string.IsNullOrWhiteSpace(billNpi))
            f.Add(new("33a-NPI", 382.6f, 746.6f, billNpi!));
        // Box 33 phone (when present, CT splits at x=492 and x=516)
        var (fArea, fNum) = SplitPhone(h.FacilityPhone);
        if (fArea is not null) f.Add(new("33-phone-area", 492.2f, 698.7f, fArea));
        if (fNum  is not null) f.Add(new("33-phone-num",  516.2f, 698.7f, fNum));

        return f;
    }

    // Locates the blank-form PNG packaged under Assets/. Returns null +
    // logs a warning if the file is missing so the renderer falls back to
    // a data-only page instead of throwing — fax with no overlay is still
    // better than a hard 500 mid-demo.
    private string? ResolveOverlayPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "CMS-1500-overlay.png");
        if (!File.Exists(path))
        {
            _logger.LogWarning("HCFA fax overlay missing at {Path} — rendering data-only.", path);
            return null;
        }
        return path;
    }

    // One positioned text element on the form. BoxId is the human-readable
    // HCFA box ref (used by the ?calibrate=true overlay for alignment checks).
    private sealed record HcfaField(string BoxId, float X, float Y, string Value, float FontSize = 9);

    // Phone formatter — strips all non-digits and splits into area code +
    // 7-digit number. CT renders phones as two separate values at two X
    // positions, no parens or hyphens, so we return them split.
    private static (string? Area, string? Number) SplitPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 10) return (null, null);
        // Take last 10 digits — handles +1 country code prefix.
        digits = digits[^10..];
        return (digits[..3], digits[3..]);
    }

    private static string JoinNonEmpty(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Fmt(DateTime? d) => d?.ToString("MM/dd/yyyy") ?? "—";
}

// Row shapes for Dapper mapping.
public sealed class HcfaHeaderRow
{
    public int PatientId { get; set; }
    public int? AccountNo { get; set; }
    public string? PatientFirstName { get; set; }
    public string? PatientMiddleName { get; set; }
    public string? PatientLastName { get; set; }
    public string? PatientSex { get; set; }
    public DateTime? PatientBirthDate { get; set; }
    public string? PatientAddress { get; set; }
    public string? PatientCity { get; set; }
    public string? PatientState { get; set; }
    public string? PatientZip { get; set; }
    public string? PatientPhone { get; set; }
    public DateTime? InjuryDate { get; set; }
    public string? AutoState { get; set; }
    public string? InsCoName { get; set; }
    public string? InsCoAddress { get; set; }
    public string? InsCoCity { get; set; }
    public string? InsCoState { get; set; }
    public string? InsCoZip { get; set; }
    public string? InsuredName { get; set; }
    public string? InsuredIdNo { get; set; }
    public string? Relationship { get; set; }
    public byte? AcceptAssignment { get; set; }
    public string? GroupNo { get; set; }
    public string? PriorAuthNo { get; set; }
    public string? BillingNpi { get; set; }
    public string? RenderingNpi { get; set; }
    public string? BillEntityName { get; set; }
    public string? BillEntityEin { get; set; }
    public string? PayToAddress1 { get; set; }
    public string? PayToCity { get; set; }
    public string? PayToState { get; set; }
    public string? PayToZip { get; set; }
    public string? BillProvName { get; set; }
    public string? BillProvPin { get; set; }
    public string? BillProvGrp { get; set; }
    public string? IcdMode { get; set; }
    public int AppointmentId { get; set; }
    public DateTime? AppointmentDateTime { get; set; }
    public int DoctorId { get; set; }
    public string? DoctorFullName { get; set; }
    public string? DoctorNpi { get; set; }
    public string? FacilityNpi { get; set; }
    public string? FacilityAddress { get; set; }
    public string? FacilityPhone { get; set; }

    public string PatientDisplayName =>
        string.Join(", ",
            new[] { PatientLastName, PatientFirstName }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool IsAutoCase => InjuryDate.HasValue;
}

public sealed class HcfaDxRow
{
    public string? Code { get; set; }
    public string? Description { get; set; }
    public int Seq { get; set; }
}

public sealed class HcfaLineRow
{
    public long TranId { get; set; }
    public DateTime? DateOfService { get; set; }
    public string? Cpt { get; set; }
    public string? Description { get; set; }
    public decimal Charge { get; set; }
}

public sealed class ChartNoteHeader
{
    public int NoteId { get; set; }
    public DateTime? NoteDate { get; set; }
    public int? TextPtr { get; set; }
    public string? DoctorName { get; set; }
    public int? DoctorId { get; set; }
}

// Signature row (image + signed timestamp) for a chart note. Class rather than a
// value tuple so the element name "Image" can't collide with QuestPDF's Image().
public sealed class SigRow
{
    public int NoteId { get; set; }
    public string? Image { get; set; }
    public DateTime? SignedAt { get; set; }
}

public sealed record HcfaNote(
    int NoteId, DateTime? NoteDate, string? DoctorName, string? PlainText,
    // ImageBase64 from the doctor's stored Signature row in PSChiro
    // (SigType='CN' for this NoteId, else fall back to the doctor's latest).
    // Rendered at the bottom of the chart-note page so the printed/faxed
    // note matches what CT outputs.
    string? SignatureImage = null,
    // SigTimestamp for the "Electronically Signed … <provider> <when>" line.
    DateTime? SignedAt = null,
    // Colored/bold runs parsed from the note RTF (preferred over PlainText for
    // rendering so the body matches ChiroTouch's blue/red formatting).
    IReadOnlyList<IReadOnlyList<RtfRun>>? RichBody = null);

public sealed record HcfaData(
    HcfaHeaderRow Header,
    IReadOnlyList<HcfaDxRow> Diagnoses,
    IReadOnlyList<HcfaLineRow> Lines,
    IReadOnlyList<HcfaNote> Notes)
{
    public decimal TotalCharge => Lines.Sum(l => l.Charge);
    public string IcdIndicator => string.Equals(Header.IcdMode, "ICD9", StringComparison.OrdinalIgnoreCase) ? "9" : "0";
}
