// One-shot: slice a page range out of a PDF.
// dotnet run --project backend/scratch/PdfSlicer -- <src> <start> <end> <dst>

using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

if (args.Length < 4) { Console.Error.WriteLine("usage: <src> <startPage 1-based> <endPage inclusive> <dst>"); return 1; }
var src = args[0]; var start = int.Parse(args[1]); var end = int.Parse(args[2]); var dst = args[3];

using var input = PdfReader.Open(src, PdfDocumentOpenMode.Import);
Console.WriteLine($"Source: {input.PageCount} pages");
if (start < 1 || end > input.PageCount || start > end) { Console.Error.WriteLine("Bad range"); return 1; }
using var output = new PdfDocument();
for (int i = start - 1; i < end; i++) output.AddPage(input.Pages[i]);
Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
output.Save(dst);
Console.WriteLine($"Wrote {output.PageCount} pages to {dst} ({new FileInfo(dst).Length / 1024.0:F1} KB)");
return 0;
