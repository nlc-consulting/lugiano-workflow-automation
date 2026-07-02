using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace Lugiano.Workflow.SyncService.Services.EobScanning;

// Splits a multi-page PDF into overlapping chunks fitting Anthropic's
// Messages-API caps (32MB / 100 pages per request). Each chunk is an in-memory
// byte[] the orchestrator streams to Claude without touching disk.
//
// Overlap matters: vision models drift at chunk boundaries (spike's
// "page 16 = AIU $373.84" hallucination was an Indemnity check blurred with an
// adjacent AIU $0 stub). A 2-page overlap gives every real EOB one non-boundary
// appearance; the orchestrator dedupes overlap rows by (page, check#, amount).
public sealed record PdfChunk(int StartPage, int EndPage, byte[] Bytes);

public static class PdfSplitter
{
    public static IReadOnlyList<PdfChunk> Split(string sourcePdfPath, int chunkSize, int overlap)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be positive.");
        if (overlap < 0 || overlap >= chunkSize)
            throw new ArgumentOutOfRangeException(nameof(overlap), "Overlap must be 0..chunkSize-1.");

        using var src = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
        var pageCount = src.PageCount;
        var chunks = new List<PdfChunk>();

        // Stride = chunkSize - overlap; e.g. size 15 + overlap 2 → strides of 13,
        // chunks 1-15, 14-28, 27-41, 40-54, ...
        var stride = chunkSize - overlap;
        for (int start = 1; start <= pageCount; start += stride)
        {
            var end = Math.Min(start + chunkSize - 1, pageCount);
            chunks.Add(BuildChunk(src, start, end));
            // Reached the end — another chunk would be all overlap.
            if (end == pageCount) break;
        }

        return chunks;
    }

    private static PdfChunk BuildChunk(PdfDocument src, int startPage, int endPage)
    {
        using var dst = new PdfDocument();
        for (int i = startPage - 1; i < endPage; i++)
            dst.AddPage(src.Pages[i]);
        using var ms = new MemoryStream();
        dst.Save(ms, closeStream: false);
        return new PdfChunk(startPage, endPage, ms.ToArray());
    }

    public static int GetPageCount(string sourcePdfPath)
    {
        using var src = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.InformationOnly);
        return src.PageCount;
    }
}
