using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Drawing;

namespace ScanArchive;

public sealed class Settings
{
    public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "扫描归档");
    public string DeviceId { get; set; } = "";
    public string Category { get; set; } = "未分类";
    public int Dpi { get; set; } = 300;
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive", "settings.json");
    public static Settings Load() => File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

public static class Archive
{
    public static string Clean(string text)
    {
        var clean = new string(text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (clean.Length > 70) clean = clean[..70];
        if (string.IsNullOrWhiteSpace(clean)) clean = "未分类";
        string stem = clean.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) clean = "_" + clean;
        return clean;
    }
    public static string Save(string root, string category, string title, string format, List<string> images, int dpi, DateTime time)
    {
        string directory = Path.Combine(Path.GetFullPath(root), time.ToString("yyyy"), time.ToString("MM"), time.ToString("dd"), Clean(category));
        Directory.CreateDirectory(directory);
        string suffix = format == "PDF" ? ".pdf" : format == "JPEG" ? ".jpg" : ".png";
        string path = Path.Combine(directory, $"{time:HHmmss_fff}_{Clean(title)}_{Guid.NewGuid().ToString("N")[..8]}{suffix}");
        string temp = path + ".partial";
        try
        {
            if (format == "PDF")
            {
                using var document = new PdfDocument();
                foreach (string file in images)
                {
                    using var img = XImage.FromFile(file);
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(img.PixelWidth * 72.0 / dpi);
                    page.Height = XUnit.FromPoint(img.PixelHeight * 72.0 / dpi);
                    using var graphics = XGraphics.FromPdfPage(page);
                    graphics.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
                }
                document.Save(temp);
            }
            else
            {
                using var img = Image.FromFile(images.Single());
                img.Save(temp, format == "JPEG" ? System.Drawing.Imaging.ImageFormat.Jpeg : System.Drawing.Imaging.ImageFormat.Png);
            }
            File.Move(temp, path);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
