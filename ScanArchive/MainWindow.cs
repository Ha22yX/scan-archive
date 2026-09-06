using System.Diagnostics;

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
    readonly Button scan = PrimaryButton("开始扫描", 210, 54);
    readonly Label status = new() { Text = "准备就绪", AutoSize = false, Dock = DockStyle.Bottom, Height = 34, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(71, 85, 105) };
    readonly Label previewHint = new() { Text = "扫描完成后在这里预览", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(100, 116, 139) };
    readonly Label listTitle = new() { Text = "扫描文件", Dock = DockStyle.Top, Height = 44, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Surface };
    readonly ListView files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, BorderStyle = BorderStyle.None };
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly Button homeNav = NavButton("主页");
    readonly Button settingsNav = NavButton("设置");
    Control? homePage;
    Control? settingsPage;
    Settings settings = new();
    CancellationTokenSource? cancellation;
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
        feeder.CheckedChanged += (_, _) => { if (feeder.Checked) format.SelectedItem = "PDF"; format.Enabled = !feeder.Checked; };
        format.Enabled = !feeder.Checked;

        var nav = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(24, 12, 24, 8), BackColor = Color.White };
        nav.Controls.Add(new Label { Text = "扫描归档", Dock = DockStyle.Left, Width = 190, Font = new Font(Font.FontFamily, 18, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft });
        var navButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 180, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        navButtons.Controls.AddRange([homeNav, settingsNav]);
        nav.Controls.Add(navButtons);
        Controls.Add(content);
        Controls.Add(nav);

        homeNav.Click += async (_, _) => { ShowHome(); await RefreshFiles(); };
        settingsNav.Click += (_, _) => ShowSettings();
        scan.Click += async (_, _) => { if (busy) cancellation?.Cancel(); else await Scan(); };
        files.Columns.Add("文件名", 250);
        files.Columns.Add("扫描时间", 145);
        files.Columns.Add("大小", 75);
        files.Resize += (_, _) => ResizeFileColumns();
        files.DoubleClick += (_, _) => OpenSelected();
        files.SelectedIndexChanged += (_, _) => PreviewSelected();
        files.ContextMenuStrip = FileMenu();
        Shown += async (_, _) => { await LoadDevices(); ShowHome(); await RefreshFiles(); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; MessageBox.Show("请等待当前页完成，程序会保存已经扫描的页面。", "正在扫描"); } };
        ShowHome();
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
        host.Controls.Add(preview);
        host.Controls.Add(previewHint);
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
            var form = new TableLayoutPanel { Dock = DockStyle.Top, Height = 360, ColumnCount = 3, RowCount = 6, Padding = new Padding(24), BackColor = Surface };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (int i = 0; i < 6; i++) form.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 5 ? 64 : 50));
            AddSetting(form, 0, "扫描设备", devices, SecondaryButton("刷新设备", async () => await LoadDevices()));
            AddSetting(form, 1, "归档目录", root, SecondaryButton("选择目录", ChooseRoot));
            AddSetting(form, 2, "分辨率", dpi, new Label { Text = "DPI · A4", AutoSize = true, Padding = new Padding(8, 7, 0, 0), ForeColor = Color.FromArgb(100, 116, 139) });
            AddSetting(form, 3, "文件格式", format, new Label());
            var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            options.Controls.AddRange([color, feeder]);
            AddSetting(form, 4, "扫描方式", options, new Label());
            var save = PrimaryButton("保存设置", 130, 42);
            save.Click += (_, _) => { try { SaveSettings(); MessageBox.Show("设置已保存。", "扫描归档"); } catch (Exception ex) { MessageBox.Show(ex.Message, "无法保存设置"); } };
            form.Controls.Add(save, 1, 5);
            page.Controls.Add(form);
            page.Controls.Add(heading);
            settingsPage = page;
        }
        content.Controls.Add(settingsPage);
        SetActiveNav(settingsNav);
    }

    static Panel Card(string title, Control body)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(18) };
        panel.Controls.Add(body);
        if (!string.IsNullOrEmpty(title))
        {
            var label = new Label { Text = title, Dock = DockStyle.Top, Height = 38, Font = new Font("Microsoft YaHei UI", 13, FontStyle.Bold) };
            panel.Controls.Add(label);
            label.BringToFront();
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
        foreach (var button in new[] { homeNav, settingsNav })
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
            foreach (string page in result.Pages) File.Delete(page);
            Directory.Delete(temp);
            string adjusted = result.ActualDpi == settings.Dpi ? "" : $" · {result.ActualDpi} DPI";
            status.Text = $"扫描完成 · {result.Pages.Count} 页{adjusted} · {Path.GetFileName(saved)}";
            await RefreshFiles();
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
        preview.Image?.Dispose();
        preview.Image = image;
        previewHint.Text = hint;
        previewHint.Visible = false;
    }

    async Task RefreshFiles()
    {
        int version = ++refreshVersion;
        string directory = settings.Root;
        try
        {
            var found = await Task.Run(() => Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(p => new[] { ".pdf", ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    .Select(p => new FileInfo(p)).OrderByDescending(p => p.CreationTimeUtc).ToList() : []);
            if (version != refreshVersion) return;
            files.BeginUpdate();
            files.Items.Clear();
            foreach (var file in found)
                files.Items.Add(new ListViewItem([file.Name, file.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"), $"{file.Length / 1024.0:N0} KB"]) { Tag = file.FullName });
            files.EndUpdate();
            int today = found.Count(f => f.CreationTime.Date == DateTime.Today);
            listTitle.Text = $"扫描文件 · 今天 {today} 份";
        }
        catch (Exception ex) { if (!busy) status.Text = "读取归档失败：" + ex.Message; }
    }

    void ResizeFileColumns()
    {
        if (files.Columns.Count != 3 || files.ClientSize.Width < 260) return;
        files.Columns[2].Width = 75;
        files.Columns[1].Width = 145;
        files.Columns[0].Width = Math.Max(140, files.ClientSize.Width - 224);
    }

    void PreviewSelected()
    {
        if (files.SelectedItems.Count == 0) return;
        string path = (string)files.SelectedItems[0].Tag!;
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            preview.Image?.Dispose();
            preview.Image = null;
            previewHint.Text = "PDF 文件\n双击文件打开查看";
            previewHint.Visible = true;
            previewHint.BringToFront();
            return;
        }
        try { ShowPreview(CreatePreview(path), Path.GetFileName(path)); } catch { }
    }

    void OpenSelected() { if (files.SelectedItems.Count > 0) Open((string)files.SelectedItems[0].Tag!); }
    static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new Exception("文件或目录不存在。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
