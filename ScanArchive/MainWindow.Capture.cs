namespace ScanArchive;

public sealed partial class MainWindow
{
    void SaveSettings()
    {
        if (!Path.IsPathFullyQualified(root.Text)) throw new Exception("请选择完整的归档目录路径。");
        settings.Root = Path.GetFullPath(root.Text);
        settings.DeviceId = (devices.SelectedItem as ScannerDevice)?.Id ?? "";
        settings.Dpi = (int)dpi.SelectedItem!;
        settings.Format = format.Text;
        settings.Feeder = feeder.Checked;
        settings.Color = color.Checked;
        settings.Save();
    }

    async Task LoadDevices()
    {
        scan.Enabled = false;
        try
        {
            var found = await Scanner.OnSta(Scanner.Devices);
            devices.Items.Clear();
            devices.Items.AddRange(found.Cast<object>().ToArray());
            devices.SelectedItem = found.FirstOrDefault(x => x.Id == settings.DeviceId) ?? found.FirstOrDefault();
            status.Text = found.Count == 0 ? "未发现扫描仪，请检查设备电源和驱动" : $"已连接 · {devices.SelectedItem}";
        }
        catch (Exception ex) { status.Text = "设备读取失败"; status.Text += "：" + ex.Message; }
        finally { scan.Enabled = true; }
    }

    async Task Scan()
    {
        string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive", "Pending", Guid.NewGuid().ToString("N"));
        try
        {
            if (devices.SelectedItem is not ScannerDevice device) throw new Exception("请先在设置中选择扫描设备。");
            var prefs=SecretaryIntegration.Preferences();
            if(prefs["libraryRoot"]!=null)root.Text=prefs["libraryRoot"]!.ToString();
            SaveSettings();
            Directory.CreateDirectory(settings.Root);
            string probe = Path.Combine(settings.Root, Guid.NewGuid() + ".tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
            DateTime started = DateTime.Now;
            busy = true;
            settingsPage?.Enabled=false;
            scan.Text = "当前页完成后停止";
            scan.BackColor = Color.FromArgb(71, 85, 105);
            settingsNav.Enabled = false;
            cancellation = new CancellationTokenSource();
            Directory.CreateDirectory(temp);
            status.Text = "正在扫描第 1 页…";
            var result = await Scanner.OnSta(() => Scanner.Capture(device.Id, settings.Dpi, settings.Feeder, settings.Color, temp, cancellation.Token,
                count => BeginInvoke((Action)(() => status.Text = $"已扫描 {count} 页，正在等待下一页…"))));
            if (result.Pages.Count == 0) { status.Text = "已停止，没有扫描文件"; return; }
            ShowPreview(CreatePreview(result.Pages[0]), $"本次扫描 · {result.Pages.Count} 页");
            string saved = await Task.Run(() => Archive.Save(settings.Root, settings.Format, result.Pages, result.ActualDpi, started));
            string handoff;
            try
            {
                SecretaryIntegration.QueueScan(saved,started,device.Id,settings.Feeder?"feeder":"flatbed");
                try{await SecretaryIntegration.FlushScans();handoff="已交给文档库分析";}
                catch{handoff="等待文档库连接，将自动重试交接";}
            }
            catch(Exception ex){throw new IOException("扫描文件已保存于 "+saved+"，但交接事件保存失败，请在文档库手动导入。",ex);}
            if (result.Warning == null)
            {
                foreach (string page in result.Pages) File.Delete(page);
                Directory.Delete(temp);
            }
            string adjusted = result.ActualDpi == settings.Dpi ? "" : $" · {result.ActualDpi} DPI";
            status.Text = $"{(result.Warning == null ? "扫描完成" : "扫描中断，已部分保存")} · {result.Pages.Count} 页{adjusted} · {handoff}";
            searchBox.Clear(); searchQuery=""; libraryOffset=0; categoryFilter.SelectedIndex=0; stateFilter.SelectedIndex=0;
            await RefreshFiles(true);
            if(files.Items.Count>0){files.Items[0].Selected=true;files.Items[0].EnsureVisible();}
            if (result.Warning != null)
                MessageBox.Show(result.Warning + "\n\n归档文件：\n" + saved + "\n\n原始页面保留在：\n" + temp,
                    "已保存扫描到的页面", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            status.Text = "扫描未完成";
            MessageBox.Show(ex.Message + (Directory.Exists(temp) ? "\n\n恢复目录：\n" + temp : ""), "扫描未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            if(settingsPage!=null)settingsPage.Enabled=true;
            scan.Text = "开始扫描";
            scan.BackColor = Accent;
            settingsNav.Enabled = true;
            cancellation?.Dispose();
            cancellation = null;
        }
    }

}
