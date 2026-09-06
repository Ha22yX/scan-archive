using System.Runtime.InteropServices;

namespace ScanArchive;

public sealed record ScannerDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public static class Scanner
{
    public static Task<T> OnSta<T>(Func<T> action)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { source.SetResult(action()); } catch (Exception ex) { source.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return source.Task;
    }
    static dynamic Create() => Activator.CreateInstance(Type.GetTypeFromProgID("WIA.DeviceManager") ?? throw new Exception("Windows WIA 服务不可用。"))!;
    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public static List<ScannerDevice> Devices()
    {
        dynamic manager = Create();
        try
        {
            var list = new List<ScannerDevice>();
            foreach (dynamic info in manager.DeviceInfos)
            {
                try { if ((int)info.Type == 1) list.Add(new ScannerDevice((string)info.DeviceID, (string)info.Properties["Name"].Value)); }
                finally { Release(info); }
            }
            return list;
        }
        finally { Release(manager); }
    }
    public static List<string> Capture(string id, int dpi, bool feeder, bool color, string folder, CancellationToken cancellation, Action<int> progress)
    {
        dynamic manager = Create();
        dynamic? device = null;
        dynamic? item = null;
        var pages = new List<string>();
        try
        {
            foreach (dynamic info in manager.DeviceInfos)
            {
                try { if ((string)info.DeviceID == id) { device = info.Connect(); break; } }
                finally { Release(info); }
            }
            if (device == null) throw new Exception("找不到所选扫描仪，请打开设备电源并刷新设备。 ");
            device.Properties[3088].Value = feeder ? 1 : 2;
            item = device.Items[1];
            item.Properties[6146].Value = color ? 1 : 2;
            item.Properties[6147].Value = dpi;
            item.Properties[6148].Value = dpi;
            // Set an A4 scan region, bounded by the driver's reported maximum.
            item.Properties[6149].Value = 0;
            item.Properties[6150].Value = 0;
            item.Properties[6151].Value = Math.Min((int)item.Properties[6151].SubTypeMax, (int)Math.Round(210.0 / 25.4 * dpi));
            item.Properties[6152].Value = Math.Min((int)item.Properties[6152].SubTypeMax, (int)Math.Round(297.0 / 25.4 * dpi));
            while (true)
            {
                if (cancellation.IsCancellationRequested) break;
                dynamic? image = null;
                try
                {
                    image = item.Transfer("{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}");
                    string path = Path.Combine(folder, $"page-{pages.Count + 1:D4}.bmp");
                    image.SaveFile(path);
                    pages.Add(path);
                    progress(pages.Count);
                }
                catch (COMException ex) when ((uint)ex.HResult == 0x80210003 && pages.Count > 0) { break; }
                finally { Release(image); }
                if (!feeder) break;
            }
            return pages;
        }
        catch (Exception ex)
        {
            throw new Exception($"扫描失败：{ex.Message}\n错误码：0x{ex.HResult:X8}\n请检查设备是否在线、进纸器是否有纸，或设备是否被其他扫描软件占用。已扫描的临时文件保留在：{folder}", ex);
        }
        finally { Release(item); Release(device); Release(manager); }
    }
}
