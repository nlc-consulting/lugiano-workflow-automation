using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lugiano.Workflow.SyncService.Workflow;
using Lugiano.Workflow.SyncService.Workflow.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// Orchestrator for EOB scan processing: save PDF, queue row, fire-and-forget
// background run (fine for the demo since scans take <2 min; move to a hosted
// service/queue for production), split into overlapping chunks, fan out to
// Claude in parallel, dedupe across overlaps, backfill reason descriptions,
// persist in one transaction, flip status to completed/failed.
public sealed class EobScanService
{
    // Bumped 15 → 25 on 2026-07-02 (7/1 cost tuning). Bigger chunks = fewer
    // API calls = less per-call fixed overhead. A dense 25-page LM lockbox
    // chunk produces ~8-12K output tokens, well under the 32K max_tokens cap.
    private const int ChunkSize = 25;
    private const int ChunkOverlap = 2;
    // Anthropic concurrency cap. Sonnet 4.5 ~$0.50/min throughput at this
    // concurrency on a typical EOB scan.
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
        // Persist the PDF for re-run + audit, foldered by scan-date-from-filename
        // (falls back to today) so runs stay organized instead of one flat dir.
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

            // Fan out chunks with a SemaphoreSlim concurrency cap. Per-chunk
            // logs surface progress during a multi-minute run.
            // Chunk failures are NON-FATAL — a transient 429/timeout on one
            // chunk shouldn't destroy successful work on the others. Failed
            // chunks are logged, results[idx] stays null, merge/persist skips nulls.
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
                            // Include the chunk range so the operator can
                            // pinpoint which pages need a rerun.
                            _logger.LogError(chunkEx,
                                "⚠️  EOB scan {ScanId} chunk {Idx}/{Total} (pp{Start}-{End}) FAILED after {Sec:F1}s — continuing with remaining chunks. Error: {ErrorMessage}",
                                scanId, idx + 1, chunks.Count, c.StartPage, c.EndPage,
                                sw.Elapsed.TotalSeconds, chunkEx.Message);
                            lock (chunkErrors)
                            {
                                chunkErrors.Add($"pp{c.StartPage}-{c.EndPage}: {chunkEx.Message}");
                            }
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

            var (dedupedChecks, dedupedLines) = MergeAndDedupe(results);
            BackfillReasonDescriptions(dedupedLines);

            // Token rollup — skip failed chunks (null results).
            int inTok = results.Where(r => r is not null).Sum(r => r!.InputTokens);
            int outTok = results.Where(r => r is not null).Sum(r => r!.OutputTokens);
            var cost = (inTok * CostInputPerMTok + outTok * CostOutputPerMTok) / 1_000_000m;

            // Score checks for hallucination + duplicate patterns before persist
            // so DB rows land pre-tagged. Rules validated on the 7/1/2026
            // reconciliation — see CheckConfidenceScorer for the pattern list.
            var scoredChecks = dedupedChecks.Select(c => new EobScanCheck
            {
                EobScanId = scanId,
                PageNumber = c.PageNumber,
                CheckNumber = c.CheckNumber,
                CheckDate = c.CheckDate,
                Amount = c.Amount,
                Payer = c.Payer,
                Administrator = c.Administrator,
            }).ToList();
            // Build the isolation set from THIS scan's line items so the scorer
            // can catch checks with no downstream line items (a strong hallucination
            // signal — Explanation-of-Review misreads, CC pages, endorsement
            // backers). Whitespace/case-insensitive comparison so "1 13 172377 J"
            // on the check matches "113172377J" as the linkage on line items.
            var isolationSet = new HashSet<string>(
                dedupedLines
                    .Where(l => !string.IsNullOrWhiteSpace(l.CheckNumber))
                    .Select(l => new string(l.CheckNumber!.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant()),
                StringComparer.Ordinal);
            CheckConfidenceScorer.Score(scoredChecks, isolationSet);
            var tierTotals = CheckConfidenceScorer.ComputeTotals(scoredChecks);
            _logger.LogInformation(
                "EOB scan {ScanId} check reconciliation: {H} high (${HA:N2}) + {M} medium (${MA:N2}) + {L} low (${LA:N2}) → clean total ${CT:N2} (raw ${RT:N2})",
                scanId, tierTotals.HighCount, tierTotals.HighAmount, tierTotals.MediumCount, tierTotals.MediumAmount,
                tierTotals.LowCount, tierTotals.LowAmount, tierTotals.CleanTotal, tierTotals.RawTotal);

            await using var tx = await db.Database.BeginTransactionAsync();
            try
            {
                foreach (var c in scoredChecks)
                {
                    db.EobScanChecks.Add(c);
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

    // Merge overlapping chunks so a check/line appearing in two adjacent
    // chunks lands in the DB once. Keys are tight enough to catch
    // re-extractions but keep Sedgwick/Indemnity pairs distinct.
    private static (List<EobExtractedCheck> checks, List<EobExtractedLineItem> lines) MergeAndDedupe(
        IReadOnlyList<EobExtractionResult?> results)
    {
        // Check key EXCLUDES page number: Claude drifts ±1 at chunk boundaries
        // (blank/duplex back pages), so including page would keep dupes as
        // separate rows. Check number + amount is enough — distinct checks with
        // the same amount get different check numbers.
        var checkSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checks = new List<EobExtractedCheck>();
        foreach (var r in results)
        {
            if (r is null) continue;
            foreach (var c in r.Checks)
            {
                var key = $"{c.CheckNumber}|{c.Amount}";
                if (checkSeen.Add(key)) checks.Add(c);
            }
        }

        // Line-item key = normalized patient + normalized DOS + CPT + billed
        // + paid. Validated: 4/22 non-lockbox 253 vs vendor's 248 (within 2%);
        // 7/2 LM lockbox needs DOS in key — every LM line is $0 paid, so
        // dropping DOS collapsed 30+ visits into one row (scan #11: 200 vs
        // vendor's 1,027). Name/DOS normalized to absorb OCR variance; claim
        // numbers stay OUT (small print, reads 3-4 ways across chunks).
        var lineSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<EobExtractedLineItem>();
        foreach (var r in results)
        {
            if (r is null) continue;
            foreach (var l in r.LineItems)
            {
                // Drop check-total / period-summary rows Claude sometimes
                // emits (empty/non-CPT code, or a DOS range). Validated
                // 6/30/2026 on 4/22 non-lockbox: drops 52 phantom rows, Paid
                // total 42% over → within 2% of vendor.
                if (!IsRealServiceLine(l)) continue;

                var key = $"{NormalizePatient(l.PatientName)}|{NormalizeDate(l.ServiceDate)}|{l.ProcedureCode}|{l.Billed}|{l.Paid}";
                if (lineSeen.Add(key)) lines.Add(l);
            }
        }

        return (checks.OrderBy(c => c.PageNumber).ToList(),
                lines.OrderBy(l => l.PageNumber).ThenBy(l => l.ProcedureCode).ToList());
    }

    // Claude shortcuts reason-code descriptions when a code repeats (seen with
    // XXU00/XXG15 on the 50-page spike run). Backfill: for any code that has a
    // description somewhere in the scan, fill it in on every other occurrence
    // that's missing one.
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

    // Reject phantom "line items" from check-total/subtotal/period-summary
    // blocks. Real CPT/HCPCS codes start with 5 digits or 1 letter + 4 digits
    // (modifiers after the leading 5 chars are fine). Empty / DOCTOR / TOTAL /
    // SUBTOTAL / SUMMARY / PATIENT / bare DOS range are the seen patterns.
    private static bool IsRealServiceLine(EobExtractedLineItem l)
    {
        var cpt = (l.ProcedureCode ?? string.Empty).Trim();
        if (cpt.Length < 5) return false;
        var upper = cpt.ToUpperInvariant();
        string[] badWords = { "DOCTOR", "TOTAL", "SUBTOTAL", "SUMMARY", "PATIENT", "CHECK" };
        foreach (var w in badWords)
            if (upper == w || upper.StartsWith(w + " ")) return false;
        // First 5 chars must be a code head: 5 digits, or 1 letter + 4 digits
        var head = cpt.Substring(0, 5).ToUpperInvariant();
        var isFiveDigits = head.All(char.IsDigit);
        var isAlphaPlus4 = char.IsLetter(head[0]) && head.AsSpan(1).ToArray().All(char.IsDigit);
        if (!isFiveDigits && !isAlphaPlus4) return false;
        // DOS with " - " between two dates = period-summary row
        var dos = l.ServiceDate ?? string.Empty;
        if (dos.Contains(" - ") && dos.Any(char.IsDigit)) return false;
        return true;
    }

    // Canonicalize a patient name for dedupe: uppercase, strip punctuation,
    // sort tokens. Absorbs case, token-order, and stray-punctuation OCR
    // variance. Hyphens preserved so "SANCHEZ-LOPEZ" doesn't collide with
    // "SANCHEZ" or "LOPEZ".
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

    // Parse many date formats to yyyy-MM-dd so the same date reported
    // differently across chunks collapses in the dedupe key. Falls back to the
    // trimmed uppercase original so unrecognizable formats still self-match.
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

    // Default: C:\ProgramData\Lugiano\EobScans (persistent, survives profile
    // purges, standard Windows service-owned data location). Override via
    // EobScan:StorageDir in config.
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
