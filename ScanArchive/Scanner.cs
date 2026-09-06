using System.Runtime.InteropServices;

namespace ScanArchive;

public sealed record ScannerDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ScanResult(List<string> Pages, int ActualDpi);

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
    public static int NormalizeValue(int requested, int subType, int min, int max, int step, IEnumerable<int> values)
    {
        var allowed = values.ToList();
        if (subType == 1)
        {
            int safeStep = Math.Max(1, step);
            int bounded = Math.Clamp(requested, min, max);
            return Math.Clamp(min + (int)Math.Round((bounded - min) / (double)safeStep) * safeStep, min, max);
        }
        if (subType == 2 && allowed.Count > 0)
            return allowed.OrderBy(value => Math.Abs((long)value - requested)).ThenByDescending(value => value).First();
        if (subType == 3 && allowed.Count > 0)
        {
            if (allowed.Contains(requested)) return requested;
            int validMask = allowed.Aggregate(0, (mask, value) => mask | value);
            return requested & validMask;
        }
        return requested;
    }
    static int SetProperty(dynamic owner, int propertyId, int requested)
    {
        dynamic? found = null;
        try
        {
            foreach (dynamic property in owner.Properties)
            {
                if ((int)property.PropertyID == propertyId) { found = property; break; }
                Release(property);
            }
            if (found == null) throw new Exception($"扫描驱动缺少必要属性 {propertyId}。");
            int subType = (int)found.SubType;
            int min = subType == 1 ? (int)found.SubTypeMin : 0;
            int max = subType == 1 ? (int)found.SubTypeMax : 0;
            int step = subType == 1 ? (int)found.SubTypeStep : 1;
            var values = new List<int>();
            if (subType is 2 or 3)
            {
                dynamic vector = found.SubTypeValues;
                try { for (int i = 1; i <= (int)vector.Count; i++) values.Add((int)vector[i]); }
                finally { Release(vector); }
            }
            int actual = NormalizeValue(requested, subType, min, max, step, values);
            found.Value = actual;
            return (int)found.Value;
        }
        finally { Release(found); }
    }
    public static ScanResult Capture(string id, int dpi, bool feeder, bool color, string folder, CancellationToken cancellation, Action<int> progress)
    {
        dynamic manager = Create();
        dynamic? device = null;
        dynamic? item = null;
        var pages = new List<string>();
        string phase = "连接扫描仪";
        int actualDpi = dpi;
        try
        {
            foreach (dynamic info in manager.DeviceInfos)
            {
                try { if ((string)info.DeviceID == id) { device = info.Connect(); break; } }
                finally { Release(info); }
            }
            if (device == null) throw new Exception("找不到所选扫描仪，请打开设备电源并刷新设备。 ");
            phase = feeder ? "选择自动进纸器" : "选择玻璃扫描平台";
            SetProperty(device, 3088, feeder ? 1 : 2);
            item = device.Items[1];
            phase = "设置颜色模式";
            SetProperty(item, 6146, color ? 1 : 2);
            phase = "设置扫描分辨率";
            actualDpi = SetProperty(item, 6147, dpi);
            SetProperty(item, 6148, actualDpi);
            // Set an A4 scan region, bounded by the driver's reported maximum.
            phase = "设置 A4 扫描范围";
            SetProperty(item, 6149, 0);
            SetProperty(item, 6150, 0);
            SetProperty(item, 6151, (int)Math.Round(210.0 / 25.4 * actualDpi));
            SetProperty(item, 6152, (int)Math.Round(297.0 / 25.4 * actualDpi));
            while (true)
            {
                if (cancellation.IsCancellationRequested) break;
                dynamic? image = null;
                try
                {
                    phase = $"扫描第 {pages.Count + 1} 页";
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
            return new ScanResult(pages, actualDpi);
        }
        catch (Exception ex)
        {
            throw new Exception($"扫描失败（{phase}）：{ex.Message}\n错误码：0x{ex.HResult:X8}\n请检查设备是否在线、进纸器是否有纸，或设备是否被其他扫描软件占用。已扫描的临时文件保留在：{folder}", ex);
        }
        finally { Release(item); Release(device); Release(manager); }
    }
}
