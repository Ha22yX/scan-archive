using NAPS2.Wia;
using ScanArchive.Integration;

namespace ScanArchive;

// WIA Automation exposes the WIA 1.0 compatibility item. Use the real WIA 2.0
// feeder and one multi-page Download so the driver owns the entire feed session.
public static class FeederScanner
{
    public static ScanResult Capture(string id, int dpi, bool color, string folder,
        CancellationToken cancellation, Action<int> progress)
    {
        var pages = new List<string>();
        int actualDpi = dpi;
        string phase = "连接 WIA 2.0 进纸器";
        string logFolder = Path.Combine(UserDataPaths.Root, "Logs");
        Directory.CreateDirectory(logFolder);
        using var log = new StreamWriter(Path.Combine(logFolder, Path.GetFileName(folder) + ".log")) { AutoFlush = true };
        log.WriteLine($"{DateTime.Now:O} WIA 2.0 feeder, requested DPI={dpi}");
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var manager = new WiaDeviceManager(WiaVersion.Wia20);
            using var device = manager.FindDevice(id);
            var items = device.GetSubItems();
            try
            {
                // IPS_PAGES is required on feeder items; don't assume that Items[1]
                // represents the feeder (the first item is often the flatbed).
                var item = items.FirstOrDefault(x => x.Properties.GetOrNull(WiaPropertyId.IPS_PAGES) != null)
                    ?? throw new InvalidOperationException("此设备没有提供 WIA 2.0 进纸器接口，请安装支持进纸器的扫描驱动。");
                phase = "设置 WIA 2.0 进纸器";
                actualDpi = Configure(item, dpi, color);
                log.WriteLine($"Item={item.Name()}, pages={item.Properties[WiaPropertyId.IPS_PAGES].Value}, DPI={actualDpi}, width={item.Properties[WiaPropertyId.IPS_XEXTENT].Value}, height={item.Properties[WiaPropertyId.IPS_YEXTENT].Value}");
                using var transfer = item.StartTransfer();
                Exception? pageError = null;
                transfer.PageScanned += (_, args) =>
                {
                    using var stream = args.Stream;
                    try
                    {
                        phase = $"保存第 {pages.Count + 1} 页";
                        string path = SavePage(stream, folder, pages.Count + 1);
                        pages.Add(path);
                        log.WriteLine($"{DateTime.Now:O} Saved page {pages.Count}");
                        progress(pages.Count);
                    }
                    catch (Exception ex)
                    {
                        pageError = ex;
                        transfer.Cancel();
                    }
                    // Preserve the current page before honoring "stop after this page".
                    if (cancellation.IsCancellationRequested) transfer.Cancel();
                    phase = $"扫描第 {pages.Count + 1} 页";
                };
                phase = "扫描第 1 页";
                for (int retry = 0; ; retry++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        bool complete = transfer.Download(); // Exactly one multi-page session.
                        if (pageError != null) throw pageError;
                        if (!complete && !cancellation.IsCancellationRequested)
                            throw new InvalidOperationException("驱动提前结束了扫描，请检查剩余纸张。");
                        break;
                    }
                    catch (WiaException ex) when (ex.ErrorCode == WiaErrorCodes.BUSY && pages.Count == 0 && retry < 10)
                    {
                        log.WriteLine($"{DateTime.Now:O} Busy before first page, retry {retry + 1}");
                        cancellation.WaitHandle.WaitOne(1000);
                    }
                    catch (WiaException ex) when (pages.Count > 0 && (ex.ErrorCode == WiaErrorCodes.PAPER_EMPTY || ex.ErrorCode == 0x00210001))
                    {
                        if (pageError != null) throw pageError;
                        break;
                    }
                }
                if (pages.Count == 0 && !cancellation.IsCancellationRequested)
                    throw new InvalidOperationException("进纸器没有返回页面，请检查是否放好纸张。");
                log.WriteLine($"{DateTime.Now:O} Finished, pages={pages.Count}, stopped={cancellation.IsCancellationRequested}");
                return new ScanResult(pages, actualDpi);
            }
            finally { foreach (var item in items) item.Dispose(); }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new ScanResult(pages, actualDpi);
        }
        catch (Exception ex)
        {
            uint code = ex is WiaException wia ? wia.ErrorCode : unchecked((uint)ex.HResult);
            log.WriteLine($"{DateTime.Now:O} {phase}: 0x{code:X8} {ex}");
            string message = $"{phase}未完成：{ex.Message}\n错误码：0x{code:X8}";
            if (pages.Count > 0)
                return new ScanResult(pages, actualDpi, message + $"\n已保存前 {pages.Count} 页，请检查剩余纸张。");
            throw new Exception(message + "\n请检查进纸器、设备连接或其他扫描软件是否占用设备。", ex);
        }
    }

    internal static int Configure(WiaItem item, int dpi, bool color)
    {
        item.Properties[WiaPropertyId.IPS_PAGES].Value = 0; // ALL_PAGES
        item.SetProperty(WiaPropertyId.IPS_DOCUMENT_HANDLING_SELECT, WiaPropertyValue.FRONT_ONLY);
        item.SetProperty(WiaPropertyId.IPA_DATATYPE, color ? 3 : 2);
        int xDpi = dpi;
        item.SetPropertyClosest(WiaPropertyId.IPS_XRES, ref xDpi);
        xDpi = (int)item.Properties[WiaPropertyId.IPS_XRES].Value;
        item.Properties[WiaPropertyId.IPS_YRES].Value = xDpi;
        item.Properties[WiaPropertyId.IPS_XPOS].Value = 0;
        item.Properties[WiaPropertyId.IPS_YPOS].Value = 0;
        SetMaximum(item.Properties[WiaPropertyId.IPS_XEXTENT]);
        SetMaximum(item.Properties[WiaPropertyId.IPS_YEXTENT]);
        if ((int)item.Properties[WiaPropertyId.IPS_PAGES].Value != 0)
            throw new InvalidOperationException("驱动未接受连续进纸模式。");
        return xDpi;
    }

    static void SetMaximum(WiaProperty property)
    {
        var a = property.Attributes;
        int type = a.Flags.HasFlag(WiaPropertyFlags.Range) ? 1 : a.Flags.HasFlag(WiaPropertyFlags.List) ? 2 : 0;
        property.Value = Scanner.MaximumValue(type, a.Min, a.Max, a.Step, a.Values?.OfType<int>() ?? []);
    }

    internal static string SavePage(Stream stream, string folder, int pageNumber)
    {
        if (stream.CanSeek) stream.Position = 0;
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        string path = Path.Combine(folder, $"page-{pageNumber:D4}.bmp");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        image.Save(output, System.Drawing.Imaging.ImageFormat.Bmp);
        return path;
    }
}
