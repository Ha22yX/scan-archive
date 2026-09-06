using System.Diagnostics;

namespace ScanArchive;

public sealed class MainWindow : Form
{
    readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    readonly TextBox root = new() { Width = 440 };
    readonly ComboBox category = new() { Width = 180 };
    readonly TextBox title = new() { Width = 230, Text = "扫描文件" };
    readonly ComboBox dpi = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox format = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox feeder = new() { Text = "自动进纸器（多页 PDF）", AutoSize = true };
    readonly CheckBox color = new() { Text = "彩色", Checked = true, AutoSize = true };
    readonly Button scan = new() { Text = "开始扫描并归档", Width = 190, Height = 42, BackColor = Color.FromArgb(35, 97, 219), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    readonly Button stop = new() { Text = "当前页完成后停止", Width = 180, Height = 42, Enabled = false };
    readonly Label status = new() { Text = "准备就绪", AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
    readonly TextBox search = new() { Width = 310, PlaceholderText = "搜索文件名、日期或分类…" };
    readonly ListView files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
    readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(235, 239, 245) };
    readonly Panel controls = new() { Dock = DockStyle.Top, Height = 248 };
    Settings settings = new();
    CancellationTokenSource? cancellation;
    bool busy;
    int refreshVersion;
    public MainWindow()
    {
        Text = "扫描归档 · Scan Archive";
        Size = new Size(1180, 800);
        MinimumSize = new Size(980, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.White;
        Padding = new Padding(22);
        try { settings = Settings.Load(); } catch (Exception ex) { Shown += (_, _) => MessageBox.Show("设置读取失败，已使用默认值。\n" + ex.Message); }
        root.Text = settings.Root;
        category.Items.AddRange(["未分类", "合同", "发票", "证件", "工作资料", "生活资料"]);
        category.Text = settings.Category;
        dpi.Items.AddRange([150, 200, 300, 600]); dpi.SelectedItem = settings.Dpi; if (dpi.SelectedIndex < 0) dpi.SelectedItem = 300;
        format.Items.AddRange(["PDF", "PNG", "JPEG"]); format.SelectedIndex = 0;
        feeder.CheckedChanged += (_, _) => { if (feeder.Checked) format.SelectedIndex = 0; format.Enabled = !feeder.Checked; };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        controls.Controls.Add(stack);
        stack.Controls.Add(new Label { Text = "扫描归档", AutoSize = true, Font = new Font(Font.FontFamily, 22, FontStyle.Bold) });
        stack.Controls.Add(Row("扫描设备", devices, Button("刷新设备", async () => await LoadDevices())));
        stack.Controls.Add(Row("归档目录", root, Button("选择目录", ChooseRoot), Button("保存设置", SaveSettings)));
        stack.Controls.Add(Row("文件分类", category, new Label { Text = "文件名称", AutoSize = true, Padding = new Padding(12, 5, 0, 0) }, title));
        stack.Controls.Add(Row("扫描选项", dpi, new Label { Text = "DPI · A4", AutoSize = true }, format, color, feeder));
        var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58 };
        commands.Controls.AddRange([scan, stop, status]);
        scan.Click += async (_, _) => await Scan();
        stop.Click += (_, _) => { cancellation?.Cancel(); stop.Enabled = false; status.Text = "将在当前页完成后停止，已扫描页面仍会归档。"; };
        var libraryBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
        libraryBar.Controls.AddRange([search, Button("刷新列表", async () => await RefreshFiles()), Button("打开文件", OpenSelected), Button("打开归档目录", () => Open(root.Text))]);
        search.TextChanged += async (_, _) => await RefreshFiles();
        files.Columns.Add("文件名称", 330); files.Columns.Add("日期 / 分类", 210); files.Columns.Add("大小", 85);
        files.DoubleClick += (_, _) => OpenSelected();
        files.SelectedIndexChanged += (_, _) => PreviewSelected();
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 690, Size = new Size(1100, 400) };
        split.Panel1.Controls.Add(files); split.Panel2.Controls.Add(preview);
        var hint = new Label { Dock = DockStyle.Bottom, Height = 28, Text = "归档规则：目录 / 年 / 月 / 日 / 分类。双击打开文件；PDF 使用系统阅读器查看。", ForeColor = Color.DimGray };
        Controls.Add(split); Controls.Add(hint); Controls.Add(libraryBar); Controls.Add(commands); Controls.Add(controls);
        Shown += async (_, _) => { await LoadDevices(); await RefreshFiles(); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; MessageBox.Show("请等待当前扫描完成，或点击停止后等待当前页保存。", "正在扫描"); } };
    }
    static FlowLayoutPanel Row(string label, params Control[] items)
    {
        var row = new FlowLayoutPanel { Width = 1080, Height = 37, WrapContents = false };
        row.Controls.Add(new Label { Text = label, Width = 88, Padding = new Padding(0, 5, 0, 0) });
        row.Controls.AddRange(items); return row;
    }
    static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32 };
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(ex.Message, "操作失败"); } };
        return button;
    }
    void ChooseRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择扫描文件的归档目录", UseDescriptionForTitle = true, SelectedPath = root.Text };
        if (dialog.ShowDialog() == DialogResult.OK) { root.Text = dialog.SelectedPath; SaveSettings(); _ = RefreshFiles(); }
    }
    void SaveSettings()
    {
        if (!Path.IsPathFullyQualified(root.Text)) throw new Exception("请选择完整的归档目录路径。");
        settings.Root = Path.GetFullPath(root.Text);
        settings.Category = Archive.Clean(category.Text);
        settings.DeviceId = (devices.SelectedItem as ScannerDevice)?.Id ?? "";
        settings.Dpi = (int)dpi.SelectedItem!;
        settings.Save();
        status.Text = "设置已保存";
    }
    async Task LoadDevices()
    {
        controls.Enabled = false;
        try
        {
            var found = await Scanner.OnSta(Scanner.Devices);
            devices.Items.Clear(); devices.Items.AddRange(found.Cast<object>().ToArray());
            devices.SelectedItem = found.FirstOrDefault(x => x.Id == settings.DeviceId) ?? found.FirstOrDefault();
            status.Text = found.Count == 0 ? "未发现扫描仪，请检查电源及 WIA 驱动。" : $"发现 {found.Count} 台扫描仪";
        }
        catch (Exception ex) { status.Text = "设备读取失败"; MessageBox.Show(ex.Message); }
        finally { controls.Enabled = true; }
    }
    async Task Scan()
    {
        if (busy) return;
        string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanArchive", "Pending", Guid.NewGuid().ToString("N"));
        try
        {
            if (devices.SelectedItem is not ScannerDevice device) throw new Exception("请先选择扫描仪。");
            SaveSettings();
            // Check destination before moving the scanner or consuming any paper.
            Directory.CreateDirectory(settings.Root);
            string probe = Path.Combine(settings.Root, Guid.NewGuid() + ".tmp");
            using (File.Create(probe)) { } File.Delete(probe);
            string destination = settings.Root, tag = settings.Category, name = title.Text, output = format.Text;
            int resolution = settings.Dpi; bool useFeeder = feeder.Checked, useColor = color.Checked;
            DateTime started = DateTime.Now;
            busy = true; scan.Enabled = false; controls.Enabled = false; stop.Enabled = true;
            cancellation = new CancellationTokenSource();
            Directory.CreateDirectory(temp);
            status.Text = "正在扫描，请等待设备完成…";
            var result = await Scanner.OnSta(() => Scanner.Capture(device.Id, resolution, useFeeder, useColor, temp, cancellation.Token,
                count => BeginInvoke((Action)(() => status.Text = $"已扫描 {count} 页…"))));
            var pages = result.Pages;
            if (pages.Count == 0) { status.Text = "已停止，未扫描页面"; return; }
            string saved = await Task.Run(() => Archive.Save(destination, tag, name, output, pages, result.ActualDpi, started));
            // Delete only this operation's known temporary files after successful archival.
            foreach (string page in pages) File.Delete(page);
            Directory.Delete(temp);
            string adjusted = result.ActualDpi == resolution ? "" : $"（设备实际使用 {result.ActualDpi} DPI）";
            status.Text = $"已归档 {pages.Count} 页{adjusted} · {Path.GetFileName(saved)}";
            await RefreshFiles();
        }
        catch (Exception ex)
        {
            status.Text = "操作失败，详情见提示";
            MessageBox.Show(ex.Message + (Directory.Exists(temp) ? "\n\n恢复目录（保留已扫描页面）：\n" + temp : ""), "扫描未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { busy = false; scan.Enabled = true; controls.Enabled = true; stop.Enabled = false; cancellation?.Dispose(); cancellation = null; }
    }
    async Task RefreshFiles()
    {
        int version = ++refreshVersion;
        string directory = root.Text, query = search.Text.Trim();
        try
        {
            var found = await Task.Run(() => Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                    .Where(p => new[] { ".pdf", ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    .Where(p => Path.GetRelativePath(directory, p).Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new FileInfo(p)).OrderByDescending(p => p.CreationTimeUtc).ToList() : []);
            if (version != refreshVersion) return;
            files.BeginUpdate(); files.Items.Clear();
            foreach (var f in found)
                files.Items.Add(new ListViewItem([f.Name, Path.GetRelativePath(directory, f.DirectoryName!), $"{f.Length / 1024.0:N0} KB"]) { Tag = f.FullName });
            files.EndUpdate();
        }
        catch (Exception ex) { if (!busy) status.Text = "读取归档失败：" + ex.Message; }
    }
    void PreviewSelected()
    {
        preview.Image?.Dispose(); preview.Image = null;
        if (files.SelectedItems.Count == 0) return;
        string path = (string)files.SelectedItems[0].Tag!;
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        try { using var img = Image.FromFile(path); preview.Image = new Bitmap(img); } catch { }
    }
    void OpenSelected() { if (files.SelectedItems.Count > 0) Open((string)files.SelectedItems[0].Tag!); }
    static void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) throw new Exception("文件或目录不存在，请先完成扫描或选择有效目录。");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
