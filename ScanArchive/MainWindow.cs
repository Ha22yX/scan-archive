using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow : Form
{
    static readonly Color Accent=Color.FromArgb(32,105,86), Ink=Color.FromArgb(27,54,51), Muted=Color.FromArgb(111,130,126), Surface=Color.FromArgb(243,246,242), Line=Color.FromArgb(222,231,225);
    readonly Panel content=new(){Dock=DockStyle.Fill,Padding=new Padding(20,8,20,16)};
    readonly Button homeNav=NavButton("工作台"), activityNav=NavButton("处理记录"), settingsNav=NavButton("设置");
    readonly Button scan=PrimaryButton("开始扫描",166,44);
    readonly Label status=new(){Text="正在连接文档库…",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,ForeColor=Muted,AutoEllipsis=true};
    readonly Label connection=new(){Text="本机文档核心",Dock=DockStyle.Bottom,Height=68,ForeColor=Color.FromArgb(170,198,185),Padding=new Padding(18),AutoEllipsis=true};
    readonly Label heading=new(){Text="文档工作台",Dock=DockStyle.Top,Height=43,Font=new Font("Microsoft YaHei UI",20,FontStyle.Bold),ForeColor=Ink};
    readonly System.Windows.Forms.Timer libraryTimer=new(){Interval=3000};
    readonly bool demo;
    bool synchronizing,busy,refreshingFiles;
    string activeView="home";
    Settings settings=new();
    CancellationTokenSource? cancellation;
    readonly CancellationTokenSource lifetime=new();
    Control? homePage,settingsPage,activityPage;

    public MainWindow(bool demo=false)
    {
        this.demo=demo;
        Text="Scan Archive · 文档工作台";
        Font=new Font("Microsoft YaHei UI",10);
        AutoScaleMode=AutoScaleMode.Dpi; BackColor=Surface; ForeColor=Ink;
        Size=new Size(1500,920);MinimumSize=new Size(1100,700);StartPosition=FormStartPosition.CenterScreen;
        KeyPreview=true;
        if(!demo)try{settings=Settings.Load();}catch(Exception ex){status.Text="设置读取失败："+ex.Message;}
        InitializeSettings();
        var side=new Panel{Dock=DockStyle.Left,Width=152,BackColor=Ink,Padding=new Padding(12,18,12,12)};
        var brand=new Label{Text="S.\nScan Archive",ForeColor=Color.White,Font=new Font(Font.FontFamily,18,FontStyle.Bold),Dock=DockStyle.Top,Height=112,Padding=new Padding(6,8,0,0)};
        var navigation=new FlowLayoutPanel{Dock=DockStyle.Top,Height=170,FlowDirection=FlowDirection.TopDown,WrapContents=false};
        navigation.Controls.AddRange([homeNav,activityNav,settingsNav]);
        side.Controls.Add(navigation);side.Controls.Add(brand);side.Controls.Add(connection);
        var shell=new Panel{Dock=DockStyle.Fill};
        var header=new Panel{Dock=DockStyle.Top,Height=112,Padding=new Padding(22,18,22,8)};
        var command=new Panel{Dock=DockStyle.Fill};
        scan.Dock=DockStyle.Right;
        command.Controls.Add(status);command.Controls.Add(scan);status.BringToFront();
        header.Controls.Add(command);header.Controls.Add(heading);command.BringToFront();
        shell.Controls.Add(content);shell.Controls.Add(header);content.BringToFront();
        Controls.Add(shell);Controls.Add(side);shell.BringToFront();
        homeNav.Click+=(_,_)=>ShowHome();
        activityNav.Click+=async(_,_)=>await RunUi(ShowActivity);
        settingsNav.Click+=async(_,_)=>await RunUi(ShowSettings);
        scan.Click+=async(_,_)=>{if(busy){cancellation?.Cancel();scan.Enabled=false;scan.Text="正在保存当前页…";}else if(!demo){ShowHome();await Scan();scan.Enabled=true;}};
        libraryTimer.Tick+=async(_,_)=>await Synchronize();
        Shown+=async(_,_)=>{if(demo){LoadDemo();return;}await Synchronize();await LoadDevices();libraryTimer.Start();};
        FormClosing+=(_,e)=>{if(busy){e.Cancel=true;status.Text="请先停止扫描，等待当前页保存完成后再关闭。";}};
        FormClosed+=(_,_)=>{lifetime.Cancel();libraryTimer.Dispose();preview.Image?.Dispose();};
        KeyDown+=async(_,e)=>{
            if(e.Control&&e.KeyCode==Keys.F){ShowHome();searchBox.Focus();e.SuppressKeyPress=true;}
            if(e.KeyCode==Keys.F5){await RunUi(async()=>{await RefreshFiles(true);await RefreshConversation();});e.Handled=true;}
            if(e.KeyCode==Keys.Delete&&files.Focused){await RunUi(DeleteSelected);e.Handled=true;}
        };
        ShowHome();
    }

    async Task RunUi(Func<Task> action)
    {
        try{await action();}
        catch(OperationCanceledException){if(!IsDisposed)status.Text="操作已取消或连接超时，可重试。";}
        catch(Exception ex){if(!IsDisposed){status.Text=ex.Message;MessageBox.Show(this,ex.Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
    }
    async Task Synchronize()
    {
        if(demo||synchronizing||IsDisposed)return;synchronizing=true;
        try {
            await SecretaryIntegration.EnsureStarted();await SecretaryIntegration.FlushScans();
            connection.Text="● 文档库已连接\n扫描与秘书已联动";
            if(activeView=="home"){await RefreshFiles();await RefreshConversation();}
            else if(activeView=="activity")await RefreshActivity();
        }catch(Exception ex){if(!IsDisposed){connection.Text="连接中 · 自动重试";if(!busy)status.Text=$"{ex.Message} · {SecretaryIntegration.PendingCount} 份扫描等待交接";}}
        finally{synchronizing=false;}
    }
    void SwitchView(Control page,string name)
    {
        content.Controls.Clear();content.Controls.Add(page);activeView=name;
        heading.Text=name switch{"settings"=>"应用设置","activity"=>"处理记录与恢复",_=>"文档工作台"};
        foreach(var (button,key) in new[]{(homeNav,"home"),(activityNav,"activity"),(settingsNav,"settings")}){
            button.BackColor=key==name?Accent:Ink;button.ForeColor=key==name?Color.White:Color.FromArgb(179,201,191);
        }
    }
    static Button NavButton(string text)=>new(){Text=text,Width=126,Height=46,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderSize=0},TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(10,0,0,0),Margin=new Padding(0,4,0,4),Cursor=Cursors.Hand};
    static Button PrimaryButton(string text,int width=100,int height=34)=>new(){Text=text,Width=width,Height=height,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderSize=0},BackColor=Accent,ForeColor=Color.White,Cursor=Cursors.Hand};
    static Button ActionButton(string text,Func<Task> action,MainWindow owner,int width=90)
    {
        var b=new Button{Text=text,Width=width,Height=34,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderColor=Line},BackColor=Color.White,Cursor=Cursors.Hand,Margin=new Padding(0,0,6,0)};
        b.Click+=async(_,_)=>{b.Enabled=false;try{await owner.RunUi(action);}finally{if(!b.IsDisposed)b.Enabled=true;}};return b;
    }
    static Label Caption(string text)=>new(){Text=text,AutoSize=false,Dock=DockStyle.Top,Height=36,TextAlign=ContentAlignment.MiddleLeft,Font=new Font("Microsoft YaHei UI",11,FontStyle.Bold)};
    internal static Panel Card(string title,Control body)
    {
        var panel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(14),Margin=new Padding(0)};
        panel.Controls.Add(body);if(title!=""){panel.Controls.Add(Caption(title));body.BringToFront();}return panel;
    }
    static string S(JsonNode? n,string key)=>n?[key]?.ToString()??"";
    static int N(JsonNode? n,string key)=>int.TryParse(S(n,key),out int v)?v:0;
    static string LocalTime(string raw)=>DateTimeOffset.TryParse(raw,out var t)?t.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"):raw;
    static string PipelineStatus(string value)=>value switch{"queued"=>"等待分析","analyzing"=>"逐页分析","indexing"=>"建立索引","analyzed"=>"等待整理","ready"=>"已归档","error"=>"需要重试","pending"=>"排队中","running"=>"处理中","done"=>"完成","failed"=>"失败","deleted"=>"已删除","cancelled"=>"已取消",_=>value};
    static void Open(string path){if(!File.Exists(path)&&!Directory.Exists(path))throw new IOException("文件或目录不存在。");Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}
}
