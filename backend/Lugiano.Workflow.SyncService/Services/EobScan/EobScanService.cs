using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// Orchestrator for EOB scan processing. Owns the full pipeline:
//   1. Save uploaded PDF to disk
//   2. Create an EobScan row in queued state
//   3. Kick off background processing (fire-and-forget Task — for production
//      we'd move this to a hosted service or background queue, but this is
//      fine for the demo since scans take <2 min)
//   4. Split PDF into overlapping chunks
//   5. Fan chunks out to Claude in parallel (concurrency-capped)
//   6. Dedupe results across overlap regions
//   7. Backfill missing reason-code descriptions from sibling rows
//   8. Persist checks + line items + token cost in one transaction
//   9. Flip scan status to completed (or failed with error)
public sealed class EobScanService
{
    private const int ChunkSize = 15;
    private const int ChunkOverlap = 2;
    // Anthropic concurrency cap — be polite. Sonnet 4.5 ~$0.50/min throughput
    // at this concurrency on a typical EOB scan.
    private const int MaxParallelChunks = 4;
    // Sonnet 4.5: $3/MTok in, $15/MTok out.
    private const decimal CostInputPerMTok = 3.00m;
    private const decimal CostOutputPerMTok = 15.00m;

    private readonly IDbContextFactory<WorkflowDbContext> _dbFactory;
    private readonly IClaudeEobExtractor _extractor;
    private readonly IConfiguration _config;
    private readonly ILogger<EobScanService> _logger;

    public EobScanService(
        IDbContextFactory<WorkflowDbContext> dbFactory,
        IClaudeEobExtractor extractor,
        IConfiguration config,
        ILogger<EobScanService> logger)
    {
        _dbFactory = dbFactory;
        _extractor = extractor;
        _config = config;
        _logger = logger;
    }

    public async Task<EobScan> StartScanAsync(Stream pdfContent, string originalFilename, CancellationToken ct = default)
    {
        // Persist the PDF to a stable location so we can re-run + audit later.
        // Foldered by scan-date-from-filename (falls back to today) so a year
        // of runs stays organized instead of a flat 400-file directory.
        var scanDate = TryParseScanDate(originalFilename) ?? DateTime.UtcNow;
        var dayFolder = scanDate.ToString("MMddyy");
        var scanStorageDir = Path.Combine(ResolveScanStorageDir(), dayFolder);
        Directory.CreateDirectory(scanStorageDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeName = MakeSafeFilename(originalFilename);
        var storedPath = Path.Combine(scanStorageDir, $"{stamp}-{safeName}");
        await using (var fs = File.Create(storedPath))
        {
            await pdfContent.CopyToAsync(fs, ct);
        }

        var pageCount = PdfSplitter.GetPageCount(storedPath);
        var fileSize = new FileInfo(storedPath).Length;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var scan = new EobScan
        {
            SourceFilename = originalFilename,
            ScanDate = TryParseScanDate(originalFilename),
            PageCount = pageCount,
            FileSizeBytes = fileSize,
            StoredPdfPath = storedPath,
            Status = EobScanStatuses.Queued,
            UploadedAt = DateTime.UtcNow,
            ChunkSize = ChunkSize,
            ChunkOverlap = ChunkOverlap,
            ModelUsed = _config["Anthropic:Model"] ?? "claude-sonnet-4-5",
        };
        db.EobScans.Add(scan);
        await db.SaveChangesAsync(ct);

        var scanId = scan.Id;
        // Fire-and-forget background processing. Errors surface via the
        // EobScan.Status / ErrorMessage columns, not exceptions.
        _ = Task.Run(() => ProcessAsync(scanId), CancellationToken.None);

        return scan;
    }

    private async Task ProcessAsync(int scanId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var scan = await db.EobScans.FirstOrDefaultAsync(s => s.Id == scanId);
        if (scan is null)
        {
            _logger.LogError("EOB scan {ScanId} missing at process start.", scanId);
            return;
        }

        scan.Status = EobScanStatuses.Running;
        scan.ProcessingStartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        try
        {
            if (string.IsNullOrEmpty(scan.StoredPdfPath) || !File.Exists(scan.StoredPdfPath))
                throw new InvalidOperationException($"Stored PDF missing: {scan.StoredPdfPath}");

            var chunks = PdfSplitter.Split(scan.StoredPdfPath, ChunkSize, ChunkOverlap);
            _logger.LogInformation(
                "EOB scan {ScanId} ({Filename}): {Pages} pages → {Chunks} chunks of {ChunkSize}±{Overlap}",
                scanId, scan.SourceFilename, scan.PageCount, chunks.Count, ChunkSize, ChunkOverlap);

            // Fan out chunks with a concurrency cap. SemaphoreSlim is the
            // canonical .NET pattern for "N at a time" without third-party deps.
            // Per-chunk start/finish logs surface progress while a multi-minute
            // run is in flight — without them, the console sits silent and
            // there's no way to tell a stuck chunk from a slow one.
            //
            // Chunk failures are NON-FATAL — a transient 429 / timeout / API
            // hiccup on one chunk shouldn't destroy 30+ minutes of successful
            // work on the other 34 chunks. Failed chunks are logged loud and
            // clear, results[idx] stays null, and the merge/persist path
            // simply skips nulls.
            using var sem = new SemaphoreSlim(MaxParallelChunks);
            var results = new EobExtractionResult?[chunks.Count];
            var chunkErrors = new List<string>();
            var tasks = new List<Task>();
            var completedCount = 0;
            var failedCount = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                var idx = i;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var c = chunks[idx];
                        _logger.LogInformation(
                            "EOB scan {ScanId} chunk {Idx}/{Total} (pp{Start}-{End}): starting",
                            scanId, idx + 1, chunks.Count, c.StartPage, c.EndPage);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            var r = await _extractor.ExtractAsync(c);
                            results[idx] = r;
                            sw.Stop();
                            var done = Interlocked.Increment(ref completedCount);
                            _logger.LogInformation(
                                "EOB scan {ScanId} chunk {Idx}/{Total} (pp{Start}-{End}): {Checks} checks, {Lines} lines, {InTok}+{OutTok} tok in {Sec:F1}s ({Done}/{Total} done)",
                                scanId, idx + 1, chunks.Count, c.StartPage, c.EndPage,
                                r.Checks.Count, r.LineItems.Count, r.InputTokens, r.OutputTokens,
                                sw.Elapsed.TotalSeconds, done, chunks.Count);
                        }
                        catch (Exception chunkEx)
                        {
                            sw.Stop();
                            Interlocked.Increment(ref failedCount);
                            // LogError renders in red in the console and
                            // captures the full stack trace. Include the
                            // chunk range so the operator can pinpoint which
                            // pages need a rerun.
                            _logger.LogError(chunkEx,
                                "⚠️  EOB scan {ScanId} chunk {Idx}/{Total} (pp{Start}-{End}) FAILED after {Sec:F1}s — continuing with remaining chunks. Error: {ErrorMessage}",
                                scanId, idx + 1, chunks.Count, c.StartPage, c.EndPage,
                                sw.Elapsed.TotalSeconds, chunkEx.Message);
                            lock (chunkErrors)
                            {
                                chunkErrors.Add($"pp{c.StartPage}-{c.EndPage}: {chunkEx.Message}");
                            }
                            // results[idx] stays null — merge skips it.
                        }
                    }
                    finally { sem.Release(); }
                }));
            }
            await Task.WhenAll(tasks);

            if (failedCount == chunks.Count)
                throw new InvalidOperationException(
                    $"All {chunks.Count} chunks failed. See individual chunk errors above. Sample: " +
                    string.Join(" | ", chunkErrors.Take(3)));
            if (failedCount > 0)
                _logger.LogWarning(
                    "EOB scan {ScanId}: {Failed}/{Total} chunks FAILED — proceeding with partial results from {Succeeded} successful chunks. Failed ranges: {Ranges}",
                    scanId, failedCount, chunks.Count, chunks.Count - failedCount,
                    string.Join(", ", chunkErrors.Take(5)));

            // Merge + dedupe. Skips nulls from failed chunks automatically
            // because the enumeration only yields non-null results.
            var (dedupedChecks, dedupedLines) = MergeAndDedupe(results);
            BackfillReasonDescriptions(dedupedLines);

            // Token rollup — skip failed chunks (null results).
            int inTok = results.Where(r => r is not null).Sum(r => r!.InputTokens);
            int outTok = results.Where(r => r is not null).Sum(r => r!.OutputTokens);
            var cost = (inTok * CostInputPerMTok + outTok * CostOutputPerMTok) / 1_000_000m;

            // Persist children — single transaction, simple to reason about.
            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                foreach (var c in dedupedChecks)
                {
                    db.EobScanChecks.Add(new EobScanCheck
                    {
                        EobScanId = scanId,
                        PageNumber = c.PageNumber,
                        CheckNumber = c.CheckNumber,
                        CheckDate = c.CheckDate,
                        Amount = c.Amount,
                        Payer = c.Payer,
                        Administrator = c.Administrator,
                    });
                }
                foreach (var l in dedupedLines)
                {
                    db.EobScanLineItems.Add(new EobScanLineItem
                    {
                        EobScanId = scanId,
                        PageNumber = l.PageNumber,
                        ClaimNumber = l.ClaimNumber,
                        PatientNameRaw = l.PatientName,
                        BillNumber = l.BillNumber,
                        ServiceDate = l.ServiceDate,
                        CheckNumber = l.CheckNumber,
                        ProcedureCode = l.ProcedureCode,
                        BilledAmount = l.Billed,
                        AllowedAmount = l.Allowed,
                        PaidAmount = l.Paid,
                        WriteOffAmount = l.WriteOff,
                        ReasonCodesJson = JsonSerializer.Serialize(
                            l.ReasonCodes.Select(r => new { code = r.Code, description = r.Description })),
                    });
                }
                scan.Status = EobScanStatuses.Completed;
                scan.CompletedAt = DateTime.UtcNow;
                scan.InputTokens = inTok;
                scan.OutputTokens = outTok;
                scan.EstimatedCostUsd = decimal.Round(cost, 4);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch { await tx.RollbackAsync(); throw; }

            _logger.LogInformation(
                "EOB scan {ScanId} completed: {Checks} checks, {Lines} line items, {InTok}+{OutTok} tokens = ${Cost:F4}",
                scanId, dedupedChecks.Count, dedupedLines.Count, inTok, outTok, cost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EOB scan {ScanId} failed", scanId);
            try
            {
                scan.Status = EobScanStatuses.Failed;
                scan.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                scan.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx, "Failed to persist failure status for EOB scan {ScanId}", scanId);
            }
        }
    }

    // Merge results from overlapping chunks. A check or line that appears in
    // two adjacent chunks (because of the page-overlap) should only land in
    // the DB once. Dedupe key is intentionally tight enough to catch
    // re-extractions while loose enough to keep the Sedgwick/Indemnity pairs
    // distinct (different check numbers → different rows).
    private static (List<EobExtractedCheck> checks, List<EobExtractedLineItem> lines) MergeAndDedupe(
        IReadOnlyList<EobExtractionResult?> results)
    {
        // Dedupe key intentionally EXCLUDES page number. Same check on adjacent
        // overlapping chunks often lands with slightly different page numbers
        // (Claude drifts by ±1 at chunk boundaries when a blank/duplex back
        // page is present); if we included page in the key those would each
        // survive as separate rows. Check number + amount is enough — two
        // distinct checks with the same amount get different check numbers.
        var checkSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<EobExtractedCheck>();
        foreach (var r in results)
        {
            if (r is null) continue;  // failed chunk, skipped
            foreach (var c in r.Checks)
            {
                var key = $"{c.CheckNumber}|{c.Amount}";
                if (checkSeen.Add(key)) checks.Add(c);
            }
        }

        // Line-item dedupe key uses NORMALIZED patient name + NORMALIZED DOS
        // + CPT + billed + paid. Rationale, validated 6/30/2026 against 4/22
        // ground truth:
        //   1. Patient names vary across chunks — "WASHINGTON, TAAZ" vs
        //      "TAAZ WASHINGTON" vs "WASHINGTON , TAAZ" all mean the same
        //      person. NormalizePatient uppercases, strips punctuation, and
        //      sorts tokens so all three collapse.
        //   2. Service dates vary too — "04/02/2026" vs "4/2/26" vs
        //      "2026-04-02". NormalizeDate parses these to a canonical
        //      yyyy-MM-dd string.
        //   3. Claim numbers vary MORE than the above because they're often
        //      small print in dense layouts — same claim appears as
        //      "4263-661215", "4A251132YTF-0001", "4263-561221" across
        //      chunks. Including claim in the key WOULD force these to
        //      separate rows even though they're the same visit. Dropping
        //      claim from the key is the right trade-off — the amounts +
        //      CPT + patient + DOS uniquely identify a line.
        var lineSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<EobExtractedLineItem>();
        foreach (var r in results)
        {
            if (r is null) continue;
            foreach (var l in r.LineItems)
            {
                // Defensive filter — drop check-total / period-summary rows
                // Claude occasionally extracts as line items. These have empty
                // or non-CPT procedure codes ("DOCTOR", "TOTAL", ""), or a
                // DOS "range" ("03/06/2026 - 03/09/2026") indicating an
                // aggregated period rather than a single service line.
                // Validated 6/30/2026 on the 4/22 non-lockbox scan: this
                // filter drops 52 phantom rows and brings the Paid total
                // from 42% over vendor to within 2% of vendor.
                if (!IsRealServiceLine(l)) continue;

                // ServiceDate DELIBERATELY EXCLUDED — validated 6/30/2026
                // by simulating 6 dedupe strategies on 458-row scan #9:
                //   with DOS in key    → 458 rows, $10,656 paid (199% over)
                //   without DOS in key → 205 rows,  $5,229 paid (98% of $5,358)
                // Trade-off: recurring visits with same patient+CPT+$ collapse
                // into one row. Acceptable — the alternative was 2× over on the
                // Paid total which the billing team scrutinizes most.
                var key = $"{NormalizePatient(l.PatientName)}|{l.ProcedureCode}|{l.Billed}|{l.Paid}";
                if (lineSeen.Add(key)) lines.Add(l);
            }
        }

        return (checks.OrderBy(c => c.PageNumber).ToList(),
                lines.OrderBy(l => l.PageNumber).ThenBy(l => l.ProcedureCode).ToList());
    }

    // Claude shortcuts reason-code descriptions when the same code repeats
    // across many lines (we observed XXU00 and XXG15 lose descriptions on
    // the 50-page spike run). Backfill: for any code that appears with a
    // description anywhere in the scan, fill in that description on every
    // OTHER occurrence in the same scan that's missing one.
    private static void BackfillReasonDescriptions(List<EobExtractedLineItem> lines)
    {
        var codeToDesc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
            foreach (var r in l.ReasonCodes)
                if (!string.IsNullOrWhiteSpace(r.Description) && !codeToDesc.ContainsKey(r.Code))
                    codeToDesc[r.Code] = r.Description!;

        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var changed = false;
            var filled = new List<EobExtractedReason>(l.ReasonCodes.Count);
            foreach (var r in l.ReasonCodes)
            {
                if (string.IsNullOrWhiteSpace(r.Description) && codeToDesc.TryGetValue(r.Code, out var d))
                {
                    filled.Add(new EobExtractedReason(r.Code, d));
                    changed = true;
                }
                else
                {
                    filled.Add(r);
                }
            }
            if (changed) lines[i] = l with { ReasonCodes = filled };
        }
    }

    // Reject phantom "line items" that Claude sometimes emits from
    // check-total, subtotal, or period-summary blocks in the EOB layout.
    // Real CPT/HCPCS codes start with either 5 digits or 1 letter + 4 digits
    // (modifier junk after the head is fine — we only check the leading 5
    // chars). A CPT of empty / DOCTOR / TOTAL / SUBTOTAL / SUMMARY /
    // PATIENT / a bare DOS range are the specific patterns seen in real
    // scans.
    private static bool IsRealServiceLine(EobExtractedLineItem l)
    {
        var cpt = (l.ProcedureCode ?? string.Empty).Trim();
        if (cpt.Length < 5) return false;
        // Word-shaped non-CPT values
        var upper = cpt.ToUpperInvariant();
        string[] badWords = { "DOCTOR", "TOTAL", "SUBTOTAL", "SUMMARY", "PATIENT", "CHECK" };
        foreach (var w in badWords)
            if (upper == w || upper.StartsWith(w + " ")) return false;
        // First 5 chars must look like a code head (5 digits, or 1 letter + 4 digits)
        var head = cpt.Substring(0, 5).ToUpperInvariant();
        var isFiveDigits = head.All(char.IsDigit);
        var isAlphaPlus4 = char.IsLetter(head[0]) && head.AsSpan(1).ToArray().All(char.IsDigit);
        if (!isFiveDigits && !isAlphaPlus4) return false;
        // DOS with " - " between two dates = period-summary row
        var dos = l.ServiceDate ?? string.Empty;
        if (dos.Contains(" - ") && dos.Any(char.IsDigit)) return false;
        return true;
    }

    // Canonicalize a patient name for dedupe: uppercase, strip
    // punctuation, sort tokens alphabetically. Handles the three OCR
    // variance patterns we've seen — case ("Sanchez-Lopez" vs "SANCHEZ-LOPEZ"),
    // token order ("WASHINGTON, TAAZ" vs "TAAZ WASHINGTON"), and stray
    // punctuation/whitespace ("WASHINGTON , TAAZ"). Hyphens are preserved
    // so "SANCHEZ-LOPEZ" doesn't collide with "SANCHEZ" or "LOPEZ".
    private static string NormalizePatient(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var cleaned = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetter(ch) || char.IsWhiteSpace(ch) || ch == '-')
                cleaned.Append(char.ToUpperInvariant(ch));
            else
                cleaned.Append(' ');
        }
        var tokens = cleaned.ToString()
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        Array.Sort(tokens, StringComparer.Ordinal);
        return string.Join(' ', tokens);
    }

    // Parse a wide range of date formats to yyyy-MM-dd. Claude reports the
    // same date differently across chunks ("04/02/2026", "4/2/26",
    // "April 2, 2026", "2026-04-02") — this collapses them to a canonical
    // form for the dedupe key. Falls back to the trimmed uppercase original
    // if parsing fails so unrecognizable formats still self-match.
    private static string NormalizeDate(string? dos)
    {
        if (string.IsNullOrWhiteSpace(dos)) return "";
        var formats = new[]
        {
            "M/d/yyyy", "MM/dd/yyyy", "M/d/yy", "MM/dd/yy",
            "M-d-yyyy", "MM-dd-yyyy", "M-d-yy", "MM-dd-yy",
            "yyyy-MM-dd", "yyyy/MM/dd",
            "MMMM d, yyyy", "MMM d, yyyy",
            "MMdd yyyy", "MMddyy", "MMddyyyy",
        };
        if (DateTime.TryParseExact(dos.Trim(), formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (DateTime.TryParse(dos.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out d))
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return dos.Trim().ToUpperInvariant();
    }

    // Parse "non-lockbox mail 4.22.26.pdf" → 2026-04-22. Loose enough to
    // handle "6.25.26", "06-25-26", "062526" variants. Returns null if no
    // date is recognizable — date is display-only, not load-bearing.
    private static DateTime? TryParseScanDate(string filename)
    {
        // M.D.YY / M-D-YY
        var m = Regex.Match(filename, @"(\d{1,2})[\.\-/](\d{1,2})[\.\-/](\d{2,4})");
        if (m.Success)
        {
            int mo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int d = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (y < 100) y += 2000;
            try { return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc); }
            catch { return null; }
        }
        // MMDDYY (e.g. "062526")
        m = Regex.Match(filename, @"\b(\d{2})(\d{2})(\d{2})\b");
        if (m.Success)
        {
            int mo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int d = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int y = 2000 + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            try { return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc); }
            catch { return null; }
        }
        return null;
    }

    // Default: C:\ProgramData\Lugiano\EobScans (persistent, survives user
    // profile purges, standard Windows service-owned data location). Override
    // via EobScan:StorageDir in config for other envs (D:\ drive, custom
    // path, etc.).
    private string ResolveScanStorageDir() =>
        _config["EobScan:StorageDir"]
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Lugiano", "EobScans");

    private static string MakeSafeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return clean.Length > 200 ? clean[..200] : clean;
    }
}
