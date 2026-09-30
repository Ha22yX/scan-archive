using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ScanArchive;

public sealed class MainWindow : Form
{
    static readonly Color Accent = Color.FromArgb(37, 99, 235);
    static readonly Color Surface = Color.FromArgb(248, 250, 252);
    readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly TextBox root = new() { Dock = DockStyle.Fill };
    readonly ComboBox dpi = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox format = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly CheckBox feeder = new() { Text = "自动进纸器（多页）", AutoSize = true };
    readonly CheckBox color = new() { Text = "彩色扫描", Checked = true, AutoSize = true };
    readonly TextBox apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "输入新密钥；留空保留已有密钥" };
    readonly DateTimePicker wakeTime = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Dock = DockStyle.Fill };
    readonly CheckBox scheduledAgent = new() { Text = "每日唤醒", Checked = true, AutoSize = true };
    readonly CheckBox autoAgent = new() { Text = "扫描分析后自动整理", Checked = true, AutoSize = true };
    readonly Button scan = PrimaryButton("开始扫描", 210, 54);
    readonly Label status = new() { Text = "准备就绪", AutoSize = false, Dock = DockStyle.Bottom, Height = 34, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(71, 85, 105) };
    readonly Label previewHint = new() { Text = "扫描完成后在这里预览", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(100, 116, 139) };
    readonly Label listTitle = new() { Text = "扫描文件", Dock = DockStyle.Top, Height = 44, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Surface };
    readonly FlowLayoutPanel pdfNavigation = new() { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Visible = false, Padding = new Padding(8, 7, 0, 0), BackColor = Color.White };
    readonly Button previousPage = new() { Text = "上一页", Width = 82, Height = 32, FlatStyle = FlatStyle.Flat };
    readonly Button nextPage = new() { Text = "下一页", Width = 82, Height = 32, FlatStyle = FlatStyle.Flat };
    readonly Label pageNumber = new() { Text = "1 / 1", Width = 86, Height = 32, TextAlign = ContentAlignment.MiddleCenter };
    readonly ListView files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, BorderStyle = BorderStyle.None };
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly Button homeNav = NavButton("主页");
    readonly Button settingsNav = NavButton("设置");
    readonly Button libraryNav = NavButton("文档库");
    readonly Button agentNav = NavButton("秘书");
    readonly System.Windows.Forms.Timer libraryTimer=new(){Interval=3000};
    WebView2? libraryView;
    bool synchronizing,refreshingFiles;
    string librarySignature="";
    Control? homePage;
    Control? settingsPage;
    Settings settings = new();
    CancellationTokenSource? cancellation;
    PdfPreviewDocument? pdfDocument;
    int pdfPageIndex;
    bool busy;
    int refreshVersion;

    public MainWindow()
    {
        Text = "扫描归档 · Scan Archive";
        Size = new Size(1180, 800);
        MinimumSize = new Size(940, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.White;
        try { settings = Settings.Load(); } catch (Exception ex) { Shown += (_, _) => MessageBox.Show("设置读取失败，已使用默认值。\n" + ex.Message); }
        root.Text = settings.Root;
        dpi.Items.AddRange([100, 200, 300]);
        dpi.SelectedItem = settings.Dpi;
        if (dpi.SelectedIndex < 0) dpi.SelectedItem = 300;
        format.Items.AddRange(["PDF", "PNG", "JPEG"]);
        format.SelectedItem = settings.Format;
        if (format.SelectedIndex < 0) format.SelectedItem = "PDF";
        feeder.Checked = settings.Feeder;
        color.Checked = settings.Color;
        try
        {
            var prefs = SecretaryIntegration.Preferences();
            if(prefs["libraryRoot"] is JsonValue libraryRoot){settings.Root=libraryRoot.ToString();root.Text=settings.Root;}
            wakeTime.Value = DateTime.Today.Add(TimeOnly.Parse(prefs["dailyWakeTime"]?.ToString() ?? "05:00").ToTimeSpan());
            scheduledAgent.Checked = prefs["scheduleEnabled"]?.GetValue<bool>() ?? true;
            autoAgent.Checked = prefs["autoOrganize"]?.GetValue<bool>() ?? true;
        }
        catch { wakeTime.Value = DateTime.Today.AddHours(5); }
        feeder.CheckedChanged += (_, _) => { if (feeder.Checked) format.SelectedItem = "PDF"; format.Enabled = !feeder.Checked; };
        format.Enabled = !feeder.Checked;

        var nav = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(24, 12, 24, 8), BackColor = Color.White };
        nav.Controls.Add(new Label { Text = "扫描归档", Dock = DockStyle.Left, Width = 190, Font = new Font(Font.FontFamily, 18, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });
        var navButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 360, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        navButtons.Controls.AddRange([homeNav, libraryNav, agentNav, settingsNav]);
        nav.Controls.Add(navButtons);
        Controls.Add(content);
        Controls.Add(nav);

        homeNav.Click += async (_, _) => { ShowHome(); await RefreshFiles(); };
        settingsNav.Click += (_, _) => ShowSettings();
        libraryNav.Click += async (_, _) => await ShowLibrary("library");
        agentNav.Click += async (_, _) => await ShowLibrary("agent");
        scan.Click += async (_, _) => { if (busy) cancellation?.Cancel(); else await Scan(); };
        files.Columns.Add("文件名", 250);
        files.Columns.Add("扫描时间", 145);
        files.Columns.Add("大小", 75);
        files.Columns.Add("处理状态",110);
        files.Resize += (_, _) => ResizeFileColumns();
        files.DoubleClick += (_, _) => OpenSelected();
        files.SelectedIndexChanged += (_, _) => PreviewSelected();
        files.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var item = files.GetItemAt(e.X, e.Y);
            if (item != null) item.Selected = true;
        };
        files.ContextMenuStrip = FileMenu();
        pdfNavigation.Controls.AddRange([previousPage, pageNumber, nextPage]);
        previousPage.Click += (_, _) => ChangePdfPage(-1);
        nextPage.Click += (_, _) => ChangePdfPage(1);
        Shown += async (_, _) => { await Synchronize(); await LoadDevices(); ShowHome(); await RefreshFiles();libraryTimer.Start(); };
        libraryTimer.Tick += async (_, _) => await Synchronize();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; MessageBox.Show("请等待当前页完成，程序会保存已经扫描的页面。", "正在扫描"); } };
        FormClosed += (_, _) => {libraryTimer.Dispose();libraryView?.Dispose();ClosePdf();};
        ShowHome();
    }

    async Task Synchronize()
    {
        if(synchronizing)return;synchronizing=true;
        try{await SecretaryIntegration.EnsureStarted();await SecretaryIntegration.FlushScans();await RefreshFiles();}
        catch(Exception ex){listTitle.Text=$"文档库连接中 · {SecretaryIntegration.PendingCount} 份待交接";if(!busy)status.Text=ex.Message;}
        finally{synchronizing=false;}
    }

    async Task ShowLibrary(string page)
    {
        try
        {
            await SecretaryIntegration.EnsureStarted();
            content.Controls.Clear();content.Padding=Padding.Empty;
            libraryView??=new WebView2{Dock=DockStyle.Fill};content.Controls.Add(libraryView);
            if(libraryView.CoreWebView2==null)
            {
                var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(SecretaryIntegration.DataRoot,"WebView2"));
                await libraryView.EnsureCoreWebView2Async(environment);
                libraryView.CoreWebView2!.Settings.AreDevToolsEnabled=false;
                libraryView.CoreWebView2.NavigationStarting+=(_,e)=>{if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||uri.Host!="localhost"||uri.Port!=5278)e.Cancel=true;};
                libraryView.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;if(Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)&&uri.Host=="localhost"&&uri.Port==5278)libraryView.CoreWebView2.Navigate(e.Uri);};
            }
            libraryView.CoreWebView2.Navigate("http://localhost:5278/#"+page);
            SetActiveNav(page=="agent"?agentNav:libraryNav);
        }
        catch(Exception ex){ShowHome();MessageBox.Show("内置文档库无法打开："+ex.Message,"文档库");}
    }

    void ShowHome()
    {
        content.Controls.Clear();
        content.Padding = new Padding(24, 12, 24, 20);
        if (homePage == null)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            var command = new Panel { Dock = DockStyle.Top, Height = 82 };
            scan.Location = new Point(0, 8);
            command.Controls.Add(scan);
            command.Controls.Add(status);
            status.Padding = new Padding(230, 0, 0, 0);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var previewCard = Card("扫描预览", PreviewArea());
            previewCard.Margin = new Padding(0, 0, 7, 0);
            var fileCard = Card("", files);
            fileCard.Margin = new Padding(7, 0, 0, 0);
            fileCard.Controls.Add(listTitle);
            grid.Controls.Add(previewCard, 0, 0);
            grid.Controls.Add(fileCard, 1, 0);
            page.Controls.Add(grid);
            page.Controls.Add(command);
            homePage = page;
        }
        content.Controls.Add(homePage);
        SetActiveNav(homeNav);
    }

    Control PreviewArea()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        var canvas = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        canvas.Controls.Add(preview);
        canvas.Controls.Add(previewHint);
        host.Controls.Add(canvas);
        host.Controls.Add(pdfNavigation);
        previewHint.BringToFront();
        return host;
    }

    void ShowSettings()
    {
        content.Controls.Clear();
        content.Padding = new Padding(34, 20, 34, 28);
        if (settingsPage == null)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, AutoScroll = true };
            var heading = new Label { Text = "设置", Dock = DockStyle.Top, Height = 58, Font = new Font(Font.FontFamily, 20, FontStyle.Bold) };
            var form = new TableLayoutPanel { Dock = DockStyle.Top, Height = 570, ColumnCount = 3, RowCount = 10, Padding = new Padding(24), BackColor = Surface };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (int i = 0; i < 10; i++) form.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 9 ? 64 : 50));
            AddSetting(form, 0, "扫描设备", devices, SecondaryButton("刷新设备", async () => await LoadDevices()));
            AddSetting(form, 1, "归档目录", root, SecondaryButton("选择目录", ChooseRoot));
            AddSetting(form, 2, "分辨率", dpi, new Label { Text = "DPI · 设备最大扫描范围", AutoSize = true, Padding = new Padding(8, 7, 0, 0), ForeColor = Color.FromArgb(100, 116, 139) });
            AddSetting(form, 3, "文件格式", format, new Label());
            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            options.Controls.AddRange([color, feeder]);
            AddSetting(form, 4, "扫描方式", options, new Label());
            AddSetting(form, 5, "OpenAI Key", apiKey, new Label { Text = SecretaryIntegration.HasKey ? "已配置密钥" : "尚未配置", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            AddSetting(form, 6, "每日唤醒", wakeTime, scheduledAgent);
            AddSetting(form, 7, "文档秘书", autoAgent, new Label());
            AddSetting(form, 8, "文档库", new Label { Text = "共用文档记录 · 搜索 · 秘书 · 整理状态", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, SecondaryButton("打开文档库", async () => await ShowLibrary("library")));
            var save = PrimaryButton("保存设置", 130, 42);
            save.Click += (_, _) => { try { SecretaryIntegration.Save(root.Text, apiKey.Text, wakeTime.Value.ToString("HH:mm"), scheduledAgent.Checked, autoAgent.Checked);SaveSettings(); apiKey.Clear(); MessageBox.Show("设置已保存。", "扫描归档"); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法保存设置"); } };
            form.Controls.Add(save, 1, 9);
            page.Controls.Add(form);
            page.Controls.Add(heading);
            settingsPage = page;
        }
        content.Controls.Add(settingsPage);
        SetActiveNav(settingsNav);
    }

    internal static Panel Card(string title, Control body)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(18) };
        panel.Controls.Add(body);
        if (!string.IsNullOrEmpty(title))
        {
            var label = new Label { Text = title, Dock = DockStyle.Top, Height = 38, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
            panel.Controls.Add(label);
            // WinForms docks in reverse z-order. Reserve the title's height
            // before laying out the fill control so it cannot cover the page.
            body.BringToFront();
        }
        return panel;
    }

    static void AddSetting(TableLayoutPanel form, int row, string label, Control input, Control action)
    {
        form.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
        input.Margin = new Padding(0, 7, 12, 7);
        form.Controls.Add(input, 1, row);
        action.Margin = new Padding(0, 7, 0, 7);
        form.Controls.Add(action, 2, row);
    }

    static Button PrimaryButton(string text, int width, int height) => new() { Text = text, Width = width, Height = height, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
    static Button NavButton(string text) => new() { Text = text, Width = 82, Height = 40, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand };
    static Button SecondaryButton(string text, Action action)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Cursor = Cursors.Hand };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "操作失败"); } };
        return button;
    }

    void SetActiveNav(Button active)
    {
        foreach (var button in new[] { homeNav, libraryNav, agentNav, settingsNav })
        {
            button.BackColor = button == active ? Color.FromArgb(239, 246, 255) : Color.White;
            button.ForeColor = button == active ? Accent : Color.FromArgb(71, 85, 105);
            button.FlatAppearance.BorderColor = button == active ? Color.FromArgb(191, 219, 254) : Color.White;
        }
    }

    ContextMenuStrip FileMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开文件", null, (_, _) => OpenSelected());
        menu.Items.Add("打开归档目录", null, (_, _) => Open(settings.Root));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("删除文件", null, async (_, _) => await DeleteSelected());
        return menu;
    }

    void ChooseRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择扫描文件的归档目录", UseDescriptionForTitle = true, SelectedPath = root.Text };
        if (dialog.ShowDialog() == DialogResult.OK) root.Text = dialog.SelectedPath;
    }

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
        settingsNav.Enabled = false;
        try
        {
            var found = await Scanner.OnSta(Scanner.Devices);
            devices.Items.Clear();
            devices.Items.AddRange(found.Cast<object>().ToArray());
            devices.SelectedItem = found.FirstOrDefault(x => x.Id == settings.DeviceId) ?? found.FirstOrDefault();
            status.Text = found.Count == 0 ? "未发现扫描仪，请检查设备电源和驱动" : $"已连接 · {devices.SelectedItem}";
        }
        catch (Exception ex) { status.Text = "设备读取失败"; MessageBox.Show(ex.Message, "扫描设备"); }
        finally { settingsNav.Enabled = !busy; }
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
            await RefreshFiles();
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
            scan.Text = "开始扫描";
            scan.BackColor = Accent;
            settingsNav.Enabled = true;
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    static Bitmap CreatePreview(string path)
    {
        using var source = Image.FromFile(path);
        double scale = Math.Min(1, Math.Min(1000.0 / source.Width, 1400.0 / source.Height));
        return new Bitmap(source, Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
    }

    void ShowPreview(Image image, string hint)
    {
        ClosePdf();
        preview.Image?.Dispose();
        preview.Image = image;
        previewHint.Text = hint;
        previewHint.Visible = false;
    }

    async Task RefreshFiles()
    {
        int version = ++refreshVersion;
        try
        {
            var found=await SecretaryIntegration.Library();
            if (version != refreshVersion) return;
            int today=found.Count(f=>DateTimeOffset.TryParse(f["scanned"]?.ToString(),out var when)&&when.LocalDateTime.Date==DateTime.Today);
            listTitle.Text=$"文档库 · 今天 {today} 份 · 待交接 {SecretaryIntegration.PendingCount}";
            string signature=string.Join('|',found.Select(f=>f.ToJsonString()));
            if(signature==librarySignature)return;librarySignature=signature;
            string? selected=files.SelectedItems.Count>0?((JsonObject)files.SelectedItems[0].Tag!)["id"]?.ToString():null;
            refreshingFiles=true;
            files.BeginUpdate();
            files.Items.Clear();
            foreach (var file in found)
            {
                string path=file["original"]!.ToString();long length=File.Exists(path)?new FileInfo(path).Length:0;
                string time=DateTimeOffset.TryParse(file["scanned"]?.ToString(),out var scanned)?scanned.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"):"";
                var item=new ListViewItem([file["title"]!.ToString(),time,$"{length/1024.0:N0} KB",PipelineStatus(file["status"]!.ToString())]){Tag=file};
                files.Items.Add(item);if(file["id"]?.ToString()==selected)item.Selected=true;
            }
            files.EndUpdate();
            refreshingFiles=false;
        }
        catch (Exception ex) { if (!busy) status.Text = "读取归档失败：" + ex.Message; }
        finally{refreshingFiles=false;}
    }

    static string PipelineStatus(string value)=>value switch{"queued"=>"等待分析","analyzing"=>"内容分析中","indexing"=>"建立搜索索引","analyzed"=>"等待秘书整理","ready"=>"已归档","error"=>"处理失败",_=>value};

    void ResizeFileColumns()
    {
        if (files.Columns.Count != 4 || files.ClientSize.Width < 260) return;
        files.Columns[2].Width = 75;
        files.Columns[1].Width = 145;files.Columns[3].Width=110;
        files.Columns[0].Width = Math.Max(140, files.ClientSize.Width - 334);
    }

    void PreviewSelected()
    {
        if (refreshingFiles||files.SelectedItems.Count == 0) return;
        string path = ((JsonObject)files.SelectedItems[0].Tag!)["original"]!.ToString();
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            OpenPdfPreview(path);
            return;
        }
        try { ShowPreview(CreatePreview(path), Path.GetFileName(path)); } catch { }
    }

    void OpenPdfPreview(string path)
    {
        ClosePdf();
        preview.Image?.Dispose();
        preview.Image = null;
        try
        {
            pdfDocument = new PdfPreviewDocument(path);
            pdfPageIndex = 0;
            RenderPdfPage();
        }
        catch (Exception ex)
        {
            ClosePdf();
            previewHint.Text = "无法预览这个 PDF\n" + ex.Message;
            previewHint.Visible = true;
            previewHint.BringToFront();
        }
    }

    void ChangePdfPage(int amount)
    {
        if (pdfDocument == null) return;
        int target = Math.Clamp(pdfPageIndex + amount, 0, pdfDocument.PageCount - 1);
        if (target == pdfPageIndex) return;
        pdfPageIndex = target;
        try { RenderPdfPage(); }
        catch (Exception ex) { MessageBox.Show("无法渲染这一页：\n" + ex.Message, "PDF 预览"); }
    }

    void RenderPdfPage()
    {
        if (pdfDocument == null) return;
        var image = pdfDocument.RenderPage(pdfPageIndex);
        preview.Image?.Dispose();
        preview.Image = image;
        previewHint.Visible = false;
        pageNumber.Text = $"{pdfPageIndex + 1} / {pdfDocument.PageCount}";
        previousPage.Enabled = pdfPageIndex > 0;
        nextPage.Enabled = pdfPageIndex + 1 < pdfDocument.PageCount;
        pdfNavigation.Visible = pdfDocument.PageCount > 1;
    }

    void ClosePdf()
    {
        pdfDocument?.Dispose();
        pdfDocument = null;
        pdfPageIndex = 0;
        pdfNavigation.Visible = false;
    }

    void OpenSelected() { if (files.SelectedItems.Count > 0) Open(((JsonObject)files.SelectedItems[0].Tag!)["original"]!.ToString()); }

    async Task DeleteSelected()
    {
        if (files.SelectedItems.Count == 0) return;
        var doc=(JsonObject)files.SelectedItems[0].Tag!;string title=doc["title"]!.ToString();
        var answer = MessageBox.Show($"要删除这个扫描文件吗？\n\n{title}\n\n移入应用回收站后，桌面和网页会同步隐藏，可在整理记录中恢复。",
            "删除扫描文件", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        try
        {
            ClosePdf();
            await SecretaryIntegration.Trash(doc["id"]!.ToString());
            preview.Image?.Dispose();
            preview.Image = null;
            previewHint.Text = "扫描完成后在这里预览";
            previewHint.Visible = true;
            previewHint.BringToFront();
            status.Text = $"已移入应用回收站 · {title}";
            await RefreshFiles();
        }
        catch (Exception ex) { MessageBox.Show("无法删除文件：\n" + ex.Message, "删除失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new Exception("文件或目录不存在。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
