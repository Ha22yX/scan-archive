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
            string pdf = Archive.Save(folder, "合同", "测试", "PDF", [image, image], 300, date);
            using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            {
                if (document.PageCount != 2) throw new Exception("PDF page count");
                if (Math.Abs(document.Pages[0].Width.Point - 144) > 1) throw new Exception("PDF dimensions");
            }
            string png = Archive.Save(folder, "../发票", "a:b", "PNG", [image], 300, date);
            using (var bitmap = Image.FromFile(png)) if (bitmap.Width != 600) throw new Exception("PNG dimensions");
            string jpg = Archive.Save(folder, "合同", "测试", "JPEG", [image], 300, date);
            using (var bitmap = Image.FromFile(jpg)) if (bitmap.Height != 900) throw new Exception("JPEG dimensions");
            string duplicate = Archive.Save(folder, "合同", "测试", "PDF", [image], 300, date);
            if (duplicate == pdf || !File.Exists(pdf)) throw new Exception("Collision protection");
            if (!pdf.Contains(Path.Combine("2026", "09", "06", "合同"))) throw new Exception("Date archive path");
            if (Archive.Clean("CON") != "_CON" || Archive.Clean("..") == "..") throw new Exception("Unsafe path");
            if (Directory.EnumerateFiles(folder, "*.partial", SearchOption.AllDirectories).Any()) throw new Exception("Partial files remain");
            var devices = Scanner.Devices();
            File.WriteAllText(report, $"PASS: multipage PDF, page dimensions, PNG, JPEG, date/category folders, unique filenames, unsafe path sanitization, atomic saves.\nWIA scanners: {string.Join(", ", devices.Select(d => d.Name))}\nTest artifacts: {folder}");
        }
        catch (Exception ex) { File.WriteAllText(report, "FAIL: " + ex); Environment.ExitCode = 1; }
    }
}
