using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly ComboBox devices=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly TextBox root=new(){Dock=DockStyle.Fill};
    readonly ComboBox dpi=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill},format=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly CheckBox feeder=new(){Text="自动进纸器（多页 PDF）",AutoSize=true},color=new(){Text="彩色扫描",Checked=true,AutoSize=true};
    readonly TextBox apiKey=new(){Dock=DockStyle.Fill,UseSystemPasswordChar=true,PlaceholderText="输入新密钥；留空保留现有密钥"};
    readonly TextBox model=new(){Dock=DockStyle.Fill},embeddingModel=new(){Dock=DockStyle.Fill},instructions=new(){Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Vertical};
    readonly NumericUpDown requestLimit=new(){Minimum=1,Maximum=100000,Value=1000,Dock=DockStyle.Fill},agentSteps=new(){Minimum=1,Maximum=50,Value=20,Dock=DockStyle.Fill};
    readonly DateTimePicker wakeTime=new(){Format=DateTimePickerFormat.Custom,CustomFormat="HH:mm",ShowUpDown=true,Dock=DockStyle.Fill};
    readonly CheckBox scheduledAgent=new(){Text="每日唤醒",AutoSize=true},autoAgent=new(){Text="每次扫描分析后自动整理",AutoSize=true};
    readonly Label keyState=new(){Text="",AutoSize=true,ForeColor=Muted};

    void InitializeSettings()
    {
        root.Text=settings.Root;dpi.Items.AddRange([100,200,300]);dpi.SelectedItem=settings.Dpi;if(dpi.SelectedIndex<0)dpi.SelectedItem=300;
        format.Items.AddRange(["PDF","PNG","JPEG"]);format.SelectedItem=settings.Format;if(format.SelectedIndex<0)format.SelectedItem="PDF";
        feeder.Checked=settings.Feeder;color.Checked=settings.Color;
        feeder.CheckedChanged+=(_,_)=>{if(feeder.Checked)format.SelectedItem="PDF";format.Enabled=!feeder.Checked;};format.Enabled=!feeder.Checked;
        var prefs=demo?new JsonObject():SecretaryIntegration.Preferences();LoadPreferences(prefs);
    }
    void LoadPreferences(JsonObject prefs)
    {
        if(S(prefs,"libraryRoot")!=""){root.Text=S(prefs,"libraryRoot");settings.Root=root.Text;}
        wakeTime.Value=DateTime.Today.Add(TimeOnly.TryParse(S(prefs,"dailyWakeTime"),out var when)?when.ToTimeSpan():TimeSpan.FromHours(5));
        scheduledAgent.Checked=prefs["scheduleEnabled"]?.GetValue<bool>()??true;autoAgent.Checked=prefs["autoOrganize"]?.GetValue<bool>()??true;
        model.Text=S(prefs,"model")==""?"gpt-6-astra":S(prefs,"model");embeddingModel.Text=S(prefs,"embeddingModel")==""?"text-embedding-3-large":S(prefs,"embeddingModel");
        instructions.Text=S(prefs,"instructions");requestLimit.Value=Math.Clamp(N(prefs,"dailyRequestLimit")==0?1000:N(prefs,"dailyRequestLimit"),1,100000);agentSteps.Value=Math.Clamp(N(prefs,"agentMaxSteps")==0?20:N(prefs,"agentMaxSteps"),1,50);
    }
    async Task ShowSettings()
    {
        if(busy){status.Text="扫描完成后可修改设置。";return;}
        if(settingsPage==null)BuildSettings();
        SwitchView(settingsPage!,"settings");
        if(demo)return;
        var data=await SecretaryIntegration.Command("settings");if(activeView!="settings")return;
        LoadPreferences(data["options"]!.AsObject());keyState.Text=data["hasApiKey"]?.GetValue<bool>()==true?"密钥已加密保存":"尚未配置密钥";
    }
    void BuildSettings()
    {
        var scroller=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.White,Padding=new Padding(22)};
        var form=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=3,Padding=new Padding(0,0,0,18)};
        form.ColumnStyles.Add(new(SizeType.Absolute,142));form.ColumnStyles.Add(new(SizeType.Percent,100));form.ColumnStyles.Add(new(SizeType.Absolute,210));
        int row=0;
        void Section(string name){form.RowStyles.Add(new(SizeType.Absolute,52));var label=Caption(name);label.Font=new Font(Font,FontStyle.Bold);form.Controls.Add(label,0,row);form.SetColumnSpan(label,3);row++;}
        void Add(string label,Control field,Control? action=null,int height=48){form.RowStyles.Add(new(SizeType.Absolute,height));form.Controls.Add(new Label{Text=label,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Muted},0,row);field.Margin=new Padding(0,8,16,6);form.Controls.Add(field,1,row);if(action!=null){action.Margin=new Padding(0,8,0,6);form.Controls.Add(action,2,row);}row++;}
        Section("扫描与文档库");
        Add("扫描设备",devices,ActionButton("刷新设备",LoadDevices,this,110));
        Add("归档目录",root,ActionButton("选择目录",()=>{using var picker=new FolderBrowserDialog{SelectedPath=root.Text,Description="文档库归档目录"};if(picker.ShowDialog(this)==DialogResult.OK)root.Text=picker.SelectedPath;return Task.CompletedTask;},this,110));
        Add("扫描精度",dpi,new Label{Text="DPI · 设备最大扫描范围",AutoSize=true,ForeColor=Muted});Add("保存格式",format);
        var options=new FlowLayoutPanel{Dock=DockStyle.Fill};options.Controls.AddRange([color,feeder]);Add("纸张来源",options,null,60);
        Section("AI 文档秘书");Add("OpenAI API Key",apiKey,keyState);Add("分析 / Agent 模型",model);Add("检索嵌入模型",embeddingModel);
        Add("每日唤醒时间",wakeTime,scheduledAgent);Add("扫描后触发",autoAgent);Add("每日请求上限",requestLimit,new Label{Text="请求次数，非金额上限",AutoSize=true,ForeColor=Muted});Add("单次 Agent 步数",agentSteps);
        Add("长期整理偏好",instructions,null,110);
        Section("远程访问");
        string addresses=string.Join("\n",NetworkInterface.GetAllNetworkInterfaces().Where(x=>x.OperationalStatus==OperationalStatus.Up).SelectMany(x=>x.GetIPProperties().UnicastAddresses).Where(x=>x.Address.AddressFamily==AddressFamily.InterNetwork&&!System.Net.IPAddress.IsLoopback(x.Address)).Select(x=>$"http://{x.Address}:5278"));
        Add("本机 / 局域网",new TextBox{Text="http://localhost:5278\r\n"+addresses.Replace("\n","\r\n"),Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,BackColor=Color.White},ActionButton("打开远程入口",()=>{Process.Start(new ProcessStartInfo("http://localhost:5278"){UseShellExecute=true});return Task.CompletedTask;},this,140),90);
        Add("访问说明",new Label{Text="网页用于手机与其他电脑访问同一文档库。首次在本机网页设置访问密码。\n电脑需保持运行，定时任务才能执行；关闭桌面窗口后文档核心继续工作。",Dock=DockStyle.Fill,ForeColor=Muted},null,78);
        var save=ActionButton("保存全部设置",SaveAllSettings,this,160);save.BackColor=Accent;save.ForeColor=Color.White;Add("",save);
        scroller.Controls.Add(form);settingsPage=scroller;
    }
    async Task SaveAllSettings()
    {
        if(busy)throw new InvalidOperationException("请等待扫描结束。");
        if(!Path.IsPathFullyQualified(root.Text))throw new ArgumentException("请选择完整的归档目录。");
        var current=await SecretaryIntegration.Command("settings");var prefs=current["options"]!.DeepClone().AsObject();
        prefs["libraryRoot"]=root.Text;prefs["model"]=model.Text.Trim();prefs["embeddingModel"]=embeddingModel.Text.Trim();prefs["dailyWakeTime"]=wakeTime.Value.ToString("HH:mm");prefs["scheduleEnabled"]=scheduledAgent.Checked;prefs["autoOrganize"]=autoAgent.Checked;prefs["dailyRequestLimit"]=(int)requestLimit.Value;prefs["agentMaxSteps"]=(int)agentSteps.Value;prefs["instructions"]=instructions.Text;
        await SecretaryIntegration.Command("save_settings",new(){["options"]=prefs,["apiKey"]=apiKey.Text});SaveSettings();apiKey.Clear();keyState.Text=SecretaryIntegration.HasKey?"密钥已加密保存":"尚未配置密钥";status.Text="全部设置已保存，下次扫描和秘书任务将使用新设置。";
    }
}
