using PdfSharp.Pdf.IO;

namespace ScanArchive;

static class SelfTest
{
    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ScanArchive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string report = Path.Combine(AppContext.BaseDirectory, "self-test.txt");
        try
        {
            string image = Path.Combine(folder, "input.bmp");
            using (var bitmap = new Bitmap(600, 900)) { using var g = Graphics.FromImage(bitmap); g.Clear(Color.White); g.DrawString("Archive test", SystemFonts.DefaultFont, Brushes.Black, 30, 30); bitmap.Save(image); }
            var date = new DateTime(2026, 9, 6, 12, 30, 0);
            string pdf = Archive.Save(folder, "PDF", [image, image], 300, date);
            using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            {
                if (document.PageCount != 2) throw new Exception("PDF page count");
                if (Math.Abs(document.Pages[0].Width.Point - 144) > 1) throw new Exception("PDF dimensions");
            }
            using (var rendered = new PdfPreviewDocument(pdf))
            {
                if (rendered.PageCount != 2) throw new Exception("PDF preview page count");
                using var page = rendered.RenderPage(1);
                if (page.Width < 1 || page.Height < 1) throw new Exception("PDF preview render");
            }
            string png = Archive.Save(folder, "PNG", [image], 300, date);
            using (var bitmap = Image.FromFile(png)) if (bitmap.Width != 600) throw new Exception("PNG dimensions");
            string jpg = Archive.Save(folder, "JPEG", [image], 300, date);
            using (var bitmap = Image.FromFile(jpg)) if (bitmap.Height != 900) throw new Exception("JPEG dimensions");
            string duplicate = Archive.Save(folder, "PDF", [image], 300, date);
            if (duplicate == pdf || !File.Exists(pdf)) throw new Exception("Collision protection");
            if (!pdf.EndsWith(Path.Combine("2026", "09", "06", "2026-09-06_12-30-00-000.pdf"))) throw new Exception("Date/time archive path");
            if (!duplicate.EndsWith("2026-09-06_12-30-00-000_002.pdf")) throw new Exception("Sequential collision suffix");
            if (Archive.Clean("CON") != "_CON" || Archive.Clean("..") == "..") throw new Exception("Unsafe path");
            if (Scanner.NormalizeValue(150, 2, 0, 0, 1, [100, 200, 300]) != 200) throw new Exception("WIA list normalization");
            if (Scanner.NormalizeValue(301, 1, 100, 600, 10, []) != 300) throw new Exception("WIA range normalization");
            if (Scanner.NormalizeValue(5, 3, 0, 0, 1, [1, 2, 4]) != 5) throw new Exception("WIA flag normalization");
            if (Directory.EnumerateFiles(folder, "*.partial", SearchOption.AllDirectories).Any()) throw new Exception("Partial files remain");
            var devices = Scanner.Devices();
            File.WriteAllText(report, $"PASS: multipage PDF, PDF page rendering/navigation data, page dimensions, PNG, JPEG, date folders, timestamp filenames, sequential collision suffix, atomic saves, WIA value normalization.\nWIA scanners: {string.Join(", ", devices.Select(d => d.Name))}\nTest artifacts: {folder}");
        }
        catch (Exception ex) { File.WriteAllText(report, "FAIL: " + ex); Environment.ExitCode = 1; }
    }
}
