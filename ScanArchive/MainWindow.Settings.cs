using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly ComboBox devices=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};
    readonly TextBox root=new(){Dock=DockStyle.Top};
    readonly ComboBox dpi=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top},format=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};
    readonly CheckBox feeder=new(){Text="使用顶部自动进纸器",AutoSize=true},color=new(){Text="保留彩色",Checked=true,AutoSize=true};
    readonly TextBox apiKey=new(){Dock=DockStyle.Top,UseSystemPasswordChar=true,PlaceholderText="输入新密钥；留空保留已保存的密钥"};
    readonly TextBox model=new(){Dock=DockStyle.Top},embeddingModel=new(){Dock=DockStyle.Top},instructions=new(){Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical,PlaceholderText="例如：汽车资料按车辆归类；账单保留商家、日期和金额。"};
    readonly NumericUpDown documentConcurrency=new(){Minimum=1,Maximum=6,Value=3,Dock=DockStyle.Top},pageConcurrency=new(){Minimum=1,Maximum=4,Value=2,Dock=DockStyle.Top};
    readonly NumericUpDown requestLimit=new(){Minimum=1,Maximum=100000,Value=1000,Dock=DockStyle.Top},agentSteps=new(){Minimum=1,Maximum=50,Value=20,Dock=DockStyle.Top};
    readonly DateTimePicker wakeTime=new(){Format=DateTimePickerFormat.Custom,CustomFormat="HH:mm",ShowUpDown=true,Dock=DockStyle.Top};
    readonly CheckBox scheduledAgent=new(){Text="每天自动检查文档库",AutoSize=true},autoAgent=new(){Text="扫描分析后自动整理归档",AutoSize=true};
    readonly Label keyState=new(){Text="",AutoSize=true,ForeColor=Muted};
    readonly Label settingsFeedback=new(){Text="设置已保存",Dock=DockStyle.Fill,ForeColor=Muted,TextAlign=ContentAlignment.MiddleLeft};
    Button? savePreferences,discardPreferences;
    readonly List<Button> settingsSections=new();
    bool settingsDirty,settingsLoading;

    void InitializeSettings()
    {
        foreach(var combo in new[]{devices,dpi,format})StyleSettingsCombo(combo);
        root.Text=settings.Root;dpi.Items.AddRange([100,200,300]);dpi.SelectedItem=settings.Dpi;if(dpi.SelectedIndex<0)dpi.SelectedItem=300;
        format.Items.AddRange(["PDF","PNG","JPEG"]);format.SelectedItem=settings.Format;if(format.SelectedIndex<0)format.SelectedItem="PDF";
        feeder.Checked=settings.Feeder;color.Checked=settings.Color;
        feeder.CheckedChanged+=(_,_)=>{if(feeder.Checked)format.SelectedItem="PDF";format.Enabled=!feeder.Checked;};format.Enabled=!feeder.Checked;
        LoadPreferences(new JsonObject());
    }
    void ReloadStartupSettings()
    {
        var restored=Settings.Load();
        var preferences=SecretaryIntegration.Preferences();
        settings=restored;
        // A delayed connection must never replace edits already made in this window.
        if(settingsDirty)return;
        settingsLoading=true;
        try{
            root.Text=settings.Root;
            dpi.SelectedItem=settings.Dpi;if(dpi.SelectedIndex<0)dpi.SelectedItem=300;
            format.SelectedItem=settings.Format;if(format.SelectedIndex<0)format.SelectedItem="PDF";
            feeder.Checked=settings.Feeder;color.Checked=settings.Color;
            if(feeder.Checked)format.SelectedItem="PDF";format.Enabled=!feeder.Checked;
        }finally{settingsLoading=false;}
        LoadPreferences(preferences);
        SetSettingsSaved();
    }
    void LoadPreferences(JsonObject prefs)
    {
        settingsLoading=true;
        try{
            if(S(prefs,"libraryRoot")!=""){root.Text=S(prefs,"libraryRoot");settings.Root=root.Text;}
            wakeTime.Value=DateTime.Today.Add(TimeOnly.TryParse(S(prefs,"dailyWakeTime"),out var when)?when.ToTimeSpan():TimeSpan.FromHours(5));
            scheduledAgent.Checked=prefs["scheduleEnabled"]?.GetValue<bool>()??true;autoAgent.Checked=prefs["autoOrganize"]?.GetValue<bool>()??true;
            model.Text=S(prefs,"model")==""?"gpt-6-astra":S(prefs,"model");embeddingModel.Text=S(prefs,"embeddingModel")==""?"text-embedding-3-large":S(prefs,"embeddingModel");
            documentConcurrency.Value=Math.Clamp(N(prefs,"documentConcurrency")==0?3:N(prefs,"documentConcurrency"),1,6);pageConcurrency.Value=Math.Clamp(N(prefs,"pageConcurrency")==0?2:N(prefs,"pageConcurrency"),1,4);
            instructions.Text=S(prefs,"instructions");requestLimit.Value=Math.Clamp(N(prefs,"dailyRequestLimit")==0?1000:N(prefs,"dailyRequestLimit"),1,100000);agentSteps.Value=Math.Clamp(N(prefs,"agentMaxSteps")==0?20:N(prefs,"agentMaxSteps"),1,50);
            wakeTime.Enabled=scheduledAgent.Checked;
        }finally{settingsLoading=false;}
    }
    async Task ShowSettings()
    {
        if(busy){status.Text="扫描完成后可修改设置。";return;}
        if(!demo&&!scannerSettingsLoaded){status.Text="正在连接文档核心并读取设置，请稍候。";return;}
        if(settingsPage==null)BuildSettings();
        SwitchView(settingsPage!,"settings");
        if(demo){keyState.Text="演示模式 · 不保存设置";return;}
        var data=await SecretaryIntegration.Command("settings");if(activeView!="settings")return;
        if(!settingsDirty)LoadPreferences(data["options"]!.AsObject());
        keyState.Text=data["hasApiKey"]?.GetValue<bool>()==true?"已加密保存 · 留空即可继续使用":"尚未配置 · 添加密钥后启用 AI";
    }
    void BuildSettings()
    {
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Surface};
        var navigation=new FlowLayoutPanel{Dock=DockStyle.Top,Height=50,WrapContents=false,BackColor=Surface,Padding=new Padding(0,0,0,10)};
        var body=new Panel{Dock=DockStyle.Fill};
        var footer=new TableLayoutPanel{Dock=DockStyle.Bottom,Height=68,ColumnCount=3,BackColor=Color.White,Padding=new Padding(18,14,12,14)};
        footer.ColumnStyles.Add(new(SizeType.Percent,100));footer.ColumnStyles.Add(new(SizeType.Absolute,118));footer.ColumnStyles.Add(new(SizeType.Absolute,136));
        discardPreferences=SettingsCommandButton("放弃修改",ResetPreferences,108);savePreferences=SettingsCommandButton("保存设置",SaveAllSettings,126);savePreferences.BackColor=Accent;savePreferences.ForeColor=Color.White;
        footer.Controls.Add(settingsFeedback,0,0);footer.Controls.Add(discardPreferences,1,0);footer.Controls.Add(savePreferences,2,0);
        page.Controls.Add(body);page.Controls.Add(footer);page.Controls.Add(navigation);body.BringToFront();
        var sections=new List<(Button button,Control panel)>();
        TableLayoutPanel Section(string title){
            var panel=new Panel{Dock=DockStyle.Fill,AutoScroll=true,Padding=new Padding(0,0,12,12),BackColor=Surface};
            var stack=new TableLayoutPanel{Dock=DockStyle.Top,ColumnCount=1,RowCount=0,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MaximumSize=new Size(1000,0),Margin=Padding.Empty,Padding=Padding.Empty};
            stack.ColumnStyles.Add(new(SizeType.Percent,100));panel.Controls.Add(stack);
            var button=new Button{Text=title,Width=154,Height=38,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderColor=Line},BackColor=Color.White,Margin=new Padding(0,0,8,0),Cursor=Cursors.Hand};
            void Activate(){body.Controls.Clear();body.Controls.Add(panel);foreach(var s in sections){s.button.BackColor=s.button==button?Accent:Color.White;s.button.ForeColor=s.button==button?Color.White:Ink;}}
            button.Click+=(_,_)=>Activate();navigation.Controls.Add(button);sections.Add((button,panel));settingsSections.Add(button);
            return stack;
        }
        var captureSection=Section("扫描与存储");var secretarySection=Section("秘书与自动化");var advancedSection=Section("高级与远程访问");
        var scannerFields=SettingsGrid(2);
        AddSettingsField(scannerFields,"扫描设备",SettingsActionRow(devices,ActionButton("刷新",LoadDevices,this,72)),"选择已安装 Windows 扫描驱动的设备。",0,0,2);
        AddSettingsField(scannerFields,"扫描精度（DPI）",dpi,"300 DPI 适合文字；较低精度扫描更快。",0,1);
        AddSettingsField(scannerFields,"保存格式",format,"进纸器多页扫描统一保存为 PDF。",1,1);
        AddSettingsField(scannerFields,"纸张来源",feeder,"勾选：顶部进纸器；取消：玻璃平台。",0,2);
        AddSettingsField(scannerFields,"图像颜色",color,"取消勾选后使用灰度扫描。",1,2);
        AddSettingsCard(captureSection,"扫描设备","放入纸张后，在工作台点击「开始扫描」。扫描范围使用设备支持的最大尺寸。",scannerFields);
        var storageFields=SettingsGrid(1);
        AddSettingsField(storageFields,"文档归档目录",SettingsActionRow(root,ActionButton("选择目录",()=>{using var picker=new FolderBrowserDialog{SelectedPath=root.Text,Description="文档库归档目录"};if(picker.ShowDialog(this)==DialogResult.OK)root.Text=picker.SelectedPath;return Task.CompletedTask;},this,102)),"扫描时间会保留在元数据中；分析完成后，秘书按内容命名并整理目录。",0,0);
        AddSettingsCard(captureSection,"文档存储","原始扫描保留用于追溯。已有文档的归档目录需要迁移，不能直接改到其他位置。",storageFields);
        var accessFields=SettingsGrid(1);
        var keyRow=SettingsActionRow(apiKey,new CheckBox{Text="显示",AutoSize=true,Width=72,Checked=false});
        ((CheckBox)keyRow.Controls[1]).CheckedChanged+=(_,_)=>apiKey.UseSystemPasswordChar=!((CheckBox)keyRow.Controls[1]).Checked;
        AddSettingsField(accessFields,"OpenAI API Key",keyRow,"密钥在此电脑加密保存；已保存的密钥不会回显。",0,0);
        keyState.Margin=new Padding(2,0,0,10);accessFields.Controls.Add(keyState,0,1);
        AddSettingsCard(secretarySection,"连接文档秘书","内容分析、归档整理和自然语言检索使用同一套 OpenAI 配置。",accessFields);
        var automationFields=SettingsGrid(2);
        AddSettingsField(automationFields,"扫描完成后",autoAgent,"先逐页理解内容，再整理、拆分与归档。",0,0,2);
        AddSettingsField(automationFields,"定时检查",scheduledAgent,"检查现有分类和文档，优化文档库。",0,1);
        AddSettingsField(automationFields,"每日唤醒时间",wakeTime,"使用此电脑的本地时间。",1,1);
        AddSettingsField(automationFields,"长期整理偏好",instructions,"秘书每次整理时都会参考这些要求。",0,2,2,154);
        AddSettingsCard(secretarySection,"自动化与整理偏好","定时唤醒需要电脑保持开机。关闭桌面窗口后，文档核心继续在后台工作。",automationFields);
        var modelFields=SettingsGrid(2);
        AddSettingsField(modelFields,"内容分析 / Agent 模型",model,"用于理解扫描内容和执行秘书任务。",0,0);
        AddSettingsField(modelFields,"搜索嵌入模型",embeddingModel,"更换后会为已有文档重建语义索引。",1,0);
        AddSettingsField(modelFields,"同时分析文档数",documentConcurrency,"1–6 份；默认 3 份。",0,1);
        AddSettingsField(modelFields,"每份文档并行页数",pageConcurrency,"1–4 页；默认 2 页。",1,1);
        AddSettingsField(modelFields,"每日请求上限",requestLimit,"限制 API 请求次数，不是金额上限。",0,2);
        AddSettingsField(modelFields,"单次秘书操作步数",agentSteps,"控制一次任务可执行的操作数量。",1,2);
        AddSettingsCard(advancedSection,"模型与处理性能","多份文档可以并行分析；归档操作依次执行，避免目录调整冲突。",modelFields);
        string addresses=demo?"http://192.168.1.20:5278":string.Join("\r\n",NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up).SelectMany(x=>x.GetIPProperties().UnicastAddresses).Where(x=>x.Address.AddressFamily==AddressFamily.InterNetwork&&!System.Net.IPAddress.IsLoopback(x.Address)).Select(x=>$"http://{x.Address}:5278").Distinct());
        var remoteFields=SettingsGrid(1);
        AddSettingsField(remoteFields,"本机与局域网访问地址",new TextBox{Text="http://localhost:5278"+(addresses.Length>0?"\r\n"+addresses:""),Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,BorderStyle=BorderStyle.FixedSingle,BackColor=Surface,ScrollBars=ScrollBars.Vertical},"手机或其他电脑与本机连接同一局域网后，可以打开局域网地址。",0,0,1,132);
        var remoteButtons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Top,Margin=new Padding(0,0,0,8)};
        remoteButtons.Controls.Add(ActionButton("打开网页入口",()=>{Process.Start(new ProcessStartInfo("http://localhost:5278"){UseShellExecute=true});return Task.CompletedTask;},this,140));
        remoteButtons.Controls.Add(ActionButton("复制访问地址",()=>{Clipboard.SetText("http://localhost:5278"+(addresses.Length>0?"\r\n"+addresses:""));settingsFeedback.Text="访问地址已复制";return Task.CompletedTask;},this,140));remoteFields.Controls.Add(remoteButtons,0,1);
        AddSettingsCard(advancedSection,"远程访问同一文档库","网页与桌面共享全部文件和处理进度。首次使用时，在本机网页设置访问密码。",remoteFields);
        sections[0].button.PerformClick();settingsPage=page;
        foreach(var c in new Control[]{root,apiKey,model,embeddingModel,instructions})c.TextChanged+=(_,_)=>MarkSettingsDirty();
        foreach(var c in new[]{devices,dpi,format})c.SelectedIndexChanged+=(_,_)=>MarkSettingsDirty();
        foreach(var c in new[]{feeder,color,scheduledAgent,autoAgent})c.CheckedChanged+=(_,_)=>MarkSettingsDirty();
        foreach(var c in new[]{requestLimit,agentSteps,documentConcurrency,pageConcurrency})c.ValueChanged+=(_,_)=>MarkSettingsDirty();
        wakeTime.ValueChanged+=(_,_)=>MarkSettingsDirty();scheduledAgent.CheckedChanged+=(_,_)=>wakeTime.Enabled=scheduledAgent.Checked;
        SetSettingsSaved();
    }
    static void StyleSettingsCombo(ComboBox combo){
        combo.DropDown+=(_,_)=>{int width=combo.Items.Cast<object>().Select(item=>TextRenderer.MeasureText(combo.GetItemText(item),combo.Font).Width+34).DefaultIfEmpty(combo.Width).Max();combo.DropDownWidth=Math.Min(Screen.FromControl(combo).WorkingArea.Width-60,Math.Max(combo.Width,width));};
    }
    static TableLayoutPanel SettingsGrid(int columns){
        var grid=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=columns,Margin=Padding.Empty,Padding=Padding.Empty};
        for(int i=0;i<columns;i++)grid.ColumnStyles.Add(new(SizeType.Percent,100f/columns));return grid;
    }
    static Control SettingsActionRow(Control field,Control action){
        var row=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};row.ColumnStyles.Add(new(SizeType.Percent,100));row.ColumnStyles.Add(new(SizeType.Absolute,action.Width+12));
        field.Dock=DockStyle.Top;field.Margin=new Padding(0,2,12,0);action.Margin=new Padding(0,0,0,0);row.Controls.Add(field,0,0);row.Controls.Add(action,1,0);return row;
    }
    static void AddSettingsField(TableLayoutPanel grid,string title,Control field,string help,int col,int row,int span=1,int height=96){
        var item=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=new Padding(0,0,18,12),Height=height};
        item.RowStyles.Add(new(SizeType.Absolute,25));item.RowStyles.Add(new(SizeType.Percent,100));item.RowStyles.Add(new(SizeType.Absolute,38));
        item.Controls.Add(new Label{Text=title,Dock=DockStyle.Fill,ForeColor=Ink,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Microsoft YaHei UI",9.5f,FontStyle.Bold)},0,0);
        field.Margin=new Padding(0,3,0,0);item.Controls.Add(field,0,1);
        item.Controls.Add(new Label{Text=help,Dock=DockStyle.Fill,ForeColor=Muted,Padding=new Padding(0,7,0,0),Font=new Font("Microsoft YaHei UI",9)},0,2);
        while(grid.RowStyles.Count<=row)grid.RowStyles.Add(new(SizeType.AutoSize));grid.Controls.Add(item,col,row);grid.SetColumnSpan(item,span);
    }
    static void AddSettingsCard(TableLayoutPanel stack,string title,string description,Control body){
        var card=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,RowCount=3,BackColor=Color.White,Padding=new Padding(22,16,4,10),Margin=new Padding(0,0,0,14),Width=850};
        var heading=new Label{Text=title,Dock=DockStyle.Top,Height=31,ForeColor=Ink,Font=new Font("Microsoft YaHei UI",12,FontStyle.Bold)};
        var hint=new Label{Text=description,Dock=DockStyle.Top,AutoSize=true,ForeColor=Muted,Margin=new Padding(0,0,18,16)};
        card.SizeChanged+=(_,_)=>hint.MaximumSize=new Size(Math.Max(200,card.ClientSize.Width-44),0);
        card.Controls.Add(heading,0,0);card.Controls.Add(hint,0,1);card.Controls.Add(body,0,2);stack.RowStyles.Add(new(SizeType.AutoSize));stack.Controls.Add(card,0,stack.RowCount++);
    }
    internal void ShowSettingsSectionForDemo(int index){if(!demo)throw new InvalidOperationException("Demo only.");if(index>=0&&index<settingsSections.Count)settingsSections[index].PerformClick();}
    Button SettingsCommandButton(string text,Func<Task> action,int width){
        var button=PrimaryButton(text,width,34);button.BackColor=Color.White;button.ForeColor=Ink;button.FlatAppearance.BorderColor=Line;button.FlatAppearance.BorderSize=1;button.Margin=new Padding(0,0,6,0);
        button.Click+=async(_,_)=>{button.Enabled=false;if(settingsPage!=null)settingsPage.Enabled=false;try{await RunUi(action);}finally{if(settingsPage is {IsDisposed:false})settingsPage.Enabled=!busy;if(!button.IsDisposed)button.Enabled=settingsDirty;}};return button;
    }
    void MarkSettingsDirty(){if(settingsLoading)return;settingsDirty=true;settingsFeedback.Text="有未保存的修改";settingsFeedback.ForeColor=Ink;if(savePreferences!=null){savePreferences.Enabled=true;savePreferences.BackColor=Accent;savePreferences.ForeColor=Color.White;}if(discardPreferences!=null)discardPreferences.Enabled=true;}
    void SetSettingsSaved(){settingsDirty=false;settingsFeedback.Text=demo?"演示模式 · 设置仅供查看":"所有设置已保存";settingsFeedback.ForeColor=Muted;if(savePreferences!=null){savePreferences.Enabled=false;savePreferences.BackColor=Line;savePreferences.ForeColor=Muted;}if(discardPreferences!=null)discardPreferences.Enabled=false;}
    async Task ResetPreferences(){
        if(demo){SetSettingsSaved();return;}
        var current=await SecretaryIntegration.Command("settings");LoadPreferences(current["options"]!.AsObject());settingsLoading=true;
        try{root.Text=settings.Root;dpi.SelectedItem=settings.Dpi;format.SelectedItem=settings.Format;color.Checked=settings.Color;feeder.Checked=settings.Feeder;devices.SelectedItem=devices.Items.OfType<ScannerDevice>().FirstOrDefault(x=>x.Id==settings.DeviceId)??devices.Items.OfType<ScannerDevice>().FirstOrDefault();apiKey.Clear();}finally{settingsLoading=false;}
        SetSettingsSaved();
    }
    async Task SaveAllSettings()
    {
        if(demo){settingsFeedback.Text="演示模式不会修改真实设置。";return;}
        if(!scannerSettingsLoaded)throw new InvalidOperationException("文档核心尚未连接，设置仍未加载，请连接成功后再保存。");
        if(!settingsDirty)return;
        if(busy)throw new InvalidOperationException("请等待扫描结束。");
        if(!Path.IsPathFullyQualified(root.Text))throw new ArgumentException("请选择完整的归档目录。");
        if(string.IsNullOrWhiteSpace(model.Text)||string.IsNullOrWhiteSpace(embeddingModel.Text))throw new ArgumentException("请填写内容分析模型与搜索嵌入模型。");
        var current=await SecretaryIntegration.Command("settings");var prefs=current["options"]!.DeepClone().AsObject();
        prefs["libraryRoot"]=root.Text;prefs["model"]=model.Text.Trim();prefs["embeddingModel"]=embeddingModel.Text.Trim();prefs["dailyWakeTime"]=wakeTime.Value.ToString("HH:mm");prefs["scheduleEnabled"]=scheduledAgent.Checked;prefs["autoOrganize"]=autoAgent.Checked;prefs["dailyRequestLimit"]=(int)requestLimit.Value;prefs["agentMaxSteps"]=(int)agentSteps.Value;prefs["instructions"]=instructions.Text;prefs["documentConcurrency"]=(int)documentConcurrency.Value;prefs["pageConcurrency"]=(int)pageConcurrency.Value;
        await SecretaryIntegration.Command("save_settings",new(){["options"]=prefs,["apiKey"]=apiKey.Text});SaveSettings();apiKey.Clear();keyState.Text=SecretaryIntegration.HasKey?"已加密保存 · 留空即可继续使用":"尚未配置 · 添加密钥后启用 AI";SetSettingsSaved();status.Text="设置已保存，下次扫描和秘书任务将使用新设置。";
    }
}
