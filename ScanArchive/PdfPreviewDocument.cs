using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace ScanArchive;

public sealed class PdfPreviewDocument : IDisposable
{
    readonly IDocReader reader;
    public int PageCount { get; }

    public PdfPreviewDocument(string path, int maxWidth = 1000, int maxHeight = 1400)
    {
        // Load into memory so selecting a PDF never locks the archived file against deletion or moving.
        reader = DocLib.Instance.GetDocReader(File.ReadAllBytes(path), new PageDimensions(maxWidth, maxHeight));
        PageCount = reader.GetPageCount();
        if (PageCount < 1) throw new InvalidDataException("PDF 中没有可预览的页面。");
    }

    public Bitmap RenderPage(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        using var page = reader.GetPageReader(pageIndex);
        byte[] pixels = page.GetImage(RenderFlags.RenderAnnotations);
        int width = page.GetPageWidth();
        int height = page.GetPageHeight();
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var area = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(area, ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            int expected = Math.Abs(data.Stride) * height;
            if (pixels.Length < expected) throw new InvalidDataException("PDF 页面渲染数据不完整。");
            Marshal.Copy(pixels, 0, data.Scan0, expected);
        }
        catch
        {
            bitmap.UnlockBits(data);
            bitmap.Dispose();
            throw;
        }
        bitmap.UnlockBits(data);
        return bitmap;
    }

    public void Dispose() => reader.Dispose();
}
