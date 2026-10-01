using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly ListView jobsList=ActivityList(),movesList=ActivityList(),trashList=ActivityList(),logList=ActivityList();
    readonly ComboBox activityFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=146};
    readonly Label activityRunning=new(),activityPending=new(),activityFailed=new(),activityCount=new(){AutoSize=true,ForeColor=Muted};
    readonly Dictionary<ListView,Label> activityEmpty=new();
    readonly Dictionary<ListView,TextBox> activityDetails=new();
    readonly Dictionary<ListView,Button> activityActions=new();
    readonly HashSet<ListView> activityActionBusy=new(),activityFilling=new();
    string activitySignature="",activityTab="jobs";
    JsonObject? activityData;
    static ListView ActivityList(){
        var images=new ImageList{ImageSize=new Size(1,42),ColorDepth=ColorDepth.Depth32Bit};images.Images.Add(new Bitmap(1,42));
        var list=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BorderStyle=BorderStyle.None,ShowItemToolTips=true,SmallImageList=images,OwnerDraw=true};
        list.DrawColumnHeader+=(_,e)=>{using var headerBrush=new SolidBrush(Surface);e.Graphics.FillRectangle(headerBrush,e.Bounds);TextRenderer.DrawText(e.Graphics,e.Header?.Text??"",list.Font,Rectangle.Inflate(e.Bounds,-10,0),Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        list.DrawItem+=(_,_)=>{};
        list.DrawSubItem+=(_,e)=>{
            bool selected=e.Item?.Selected==true;var background=selected?Color.FromArgb(225,240,234):(e.ItemIndex%2==0?Color.White:Color.FromArgb(249,250,248));
            using var brush=new SolidBrush(background);e.Graphics.FillRectangle(brush,e.Bounds);
            var foreground=selected?Ink:e.SubItem?.ForeColor??Ink;
            TextRenderer.DrawText(e.Graphics,e.SubItem?.Text??"",list.Font,Rectangle.Inflate(e.Bounds,-10,0),foreground,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        };
        list.Disposed+=(_,_)=>images.Dispose();return list;
    }
    async Task ShowActivity()
    {
        if(activityPage==null)BuildActivity();
        SwitchView(activityPage!,"activity");
        if(demo){LoadActivityDemo();return;}
        await RefreshActivity();
    }
    void BuildActivity()
    {
        var page=new Panel{Dock=DockStyle.Fill,BackColor=Surface};
        var summary=new TableLayoutPanel{Dock=DockStyle.Top,Height=90,ColumnCount=3,Margin=Padding.Empty};
        for(int i=0;i<3;i++)summary.ColumnStyles.Add(new(SizeType.Percent,33.333f));
        void Counter(Label number,string caption,int column,Color shade,int filter){
            var card=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Margin=new Padding(0,0,column==2?0:12,12),Padding=new Padding(18,8,12,8),Cursor=Cursors.Hand};
            number.Text="0";number.Font=new Font("Microsoft YaHei UI",20,FontStyle.Bold);number.ForeColor=shade;number.Dock=DockStyle.Top;number.Height=37;
            var label=new Label{Text=caption,Dock=DockStyle.Fill,ForeColor=Muted};card.Controls.Add(label);card.Controls.Add(number);label.BringToFront();
            foreach(Control control in new Control[]{card,number,label})control.Click+=(_,_)=>{activityFilter.SelectedIndex=filter;ShowActivityTab("jobs");};summary.Controls.Add(card,column,0);
        }
        Counter(activityRunning,"正在处理",0,Accent,1);Counter(activityPending,"等待处理",1,Ink,1);Counter(activityFailed,"需要关注",2,Color.FromArgb(174,83,54),2);
        var navigation=new FlowLayoutPanel{Dock=DockStyle.Top,Height=46,BackColor=Surface,WrapContents=false};
        var toolbar=new TableLayoutPanel{Dock=DockStyle.Top,Height=54,ColumnCount=4,Padding=new Padding(14,8,10,8),BackColor=Color.White};
        toolbar.ColumnStyles.Add(new(SizeType.Absolute,158));toolbar.ColumnStyles.Add(new(SizeType.Percent,100));toolbar.ColumnStyles.Add(new(SizeType.Absolute,104));toolbar.ColumnStyles.Add(new(SizeType.Absolute,158));
        StyleSettingsCombo(activityFilter);activityFilter.Items.AddRange(["全部任务","进行中与排队","需要关注","已完成"]);activityFilter.SelectedIndex=0;activityFilter.Margin=new Padding(0,3,10,0);activityCount.Margin=new Padding(2,9,0,0);
        activityFilter.SelectedIndexChanged+=(_,_)=>RenderActivity();
        toolbar.Controls.Add(activityFilter,0,0);toolbar.Controls.Add(activityCount,1,0);toolbar.Controls.Add(ActionButton("刷新",RefreshActivity,this,94),2,0);
        toolbar.Controls.Add(ActionButton("检查整个文档库",async()=>{if(demo)return;await SecretaryIntegration.Command("maintenance");status.Text="文档库检查已加入队列，秘书将检查分类结构与检索质量。";await RefreshActivity();},this,148),3,0);
        var body=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(14,0,14,12)};
        activityPanels.Clear();activityTabButtons.Clear();
        void Add(string key,string title,ListView list,string[] columns,string hint,string action,string command){
            var nav=new Button{Text=title,Width=130,Height=36,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderColor=Line},BackColor=Color.White,Margin=new Padding(0,0,8,0),Cursor=Cursors.Hand};
            navigation.Controls.Add(nav);activityTabButtons[key]=nav;nav.Click+=(_,_)=>ShowActivityTab(key);
            var panel=new Panel{Dock=DockStyle.Fill,BackColor=Color.White};
            foreach(string column in columns)list.Columns.Add(column,150);
            list.SizeChanged+=(_,_)=>SizeActivityColumns(list);
            var listHost=new Panel{Dock=DockStyle.Fill,BackColor=Color.White};
            var empty=new Label{Text="暂无记录",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Muted};activityEmpty[list]=empty;
            listHost.Controls.Add(list);listHost.Controls.Add(empty);list.BringToFront();
            var detail=new Panel{Dock=DockStyle.Bottom,Height=162,BackColor=Surface,Padding=new Padding(12)};
            var detailHeading=new Label{Text="记录详情",Dock=DockStyle.Top,Height=24,ForeColor=Ink,Font=new Font("Microsoft YaHei UI",9.5f,FontStyle.Bold)};
            var detailText=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Surface,ForeColor=Muted,ScrollBars=ScrollBars.Vertical,Text=hint};activityDetails[list]=detailText;
            var detailFooter=new Panel{Dock=DockStyle.Bottom,Height=36};
            var helper=new Label{Text=hint,Dock=DockStyle.Fill,ForeColor=Muted,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};detailFooter.Controls.Add(helper);
            if(action!=""){
                var button=ActivityCommandButton(action,list,command);button.Dock=DockStyle.Right;button.Enabled=false;detailFooter.Controls.Add(button);helper.BringToFront();activityActions[list]=button;
            }
            detail.Controls.Add(detailText);detail.Controls.Add(detailHeading);detail.Controls.Add(detailFooter);detailText.BringToFront();
            panel.Controls.Add(listHost);panel.Controls.Add(detail);listHost.BringToFront();activityPanels[key]=(panel,body);
            list.SelectedIndexChanged+=(_,_)=>UpdateActivitySelection(list);list.DoubleClick+=(_,_)=>{if(list.SelectedItems.Count>0){using var dialog=new Form{Text="完整记录",StartPosition=FormStartPosition.CenterParent,Size=new Size(800,520),MinimumSize=new Size(520,340),Font=Font};dialog.Controls.Add(new TextBox{Text=ActivityRecordText(list,(JsonNode)list.SelectedItems[0].Tag!),Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,BorderStyle=BorderStyle.None});dialog.ShowDialog(this);}};
        }
        Add("jobs","任务队列",jobsList,["任务","状态","文档 / 内容","更新时间"],"选择任务可查看完整状态与错误原因。","重试此任务","retry_job");
        Add("moves","归档变更",movesList,["时间","状态","归档位置","说明"],"选择归档或合并记录，查看依据并撤销。","撤销此变更","undo");
        Add("trash","回收站",trashList,["文档","删除时间"],"删除的文档可在这里恢复到文档库。","恢复此文档","restore");
        Add("log","活动日志",logList,["时间","类型","说明"],"选择记录查看详细信息；双击可在独立窗口阅读全文。","","");
        page.Controls.Add(body);page.Controls.Add(toolbar);page.Controls.Add(navigation);page.Controls.Add(summary);body.BringToFront();activityPage=page;ShowActivityTab("jobs");
    }
    readonly Dictionary<string,(Control panel,Panel host)> activityPanels=new();
    readonly Dictionary<string,Button> activityTabButtons=new();
    void ShowActivityTab(string key){
        activityTab=key;if(!activityPanels.TryGetValue(key,out var value))return;value.host.Controls.Clear();value.host.Controls.Add(value.panel);
        foreach(var pair in activityTabButtons){pair.Value.BackColor=pair.Key==key?Accent:Color.White;pair.Value.ForeColor=pair.Key==key?Color.White:Ink;}
        activityFilter.Visible=key=="jobs";RenderActivity();
    }
    static void SizeActivityColumns(ListView view){
        int available=Math.Max(420,view.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-2);if(view.Columns.Count==4){
            int first=142;view.Columns[0].Width=first;view.Columns[1].Width=100;
            int last=view.Columns[3].Text=="更新时间"?178:Math.Max(160,(available-first-100)/2);view.Columns[3].Width=last;view.Columns[2].Width=Math.Max(160,available-first-100-last);
        }else if(view.Columns.Count==3){view.Columns[0].Width=178;view.Columns[1].Width=128;view.Columns[2].Width=Math.Max(200,available-306);}
        else if(view.Columns.Count==2){view.Columns[1].Width=178;view.Columns[0].Width=Math.Max(220,available-178);}
    }
    async Task RefreshActivity()
    {
        if(demo||activityPage==null)return;
        var data=await SecretaryIntegration.Command("activity",ct:lifetime.Token);if(IsDisposed)return;
        string signature=data.ToJsonString();if(signature==activitySignature)return;activitySignature=signature;activityData=data;RenderActivity();
    }
    void RenderActivity(){
        if(activityData==null)return;
        var allJobs=activityData["jobs"]!.AsArray().Where(x=>x!=null).Cast<JsonNode>().ToArray();
        int Count(string state)=>int.TryParse(S(activityData["counts"],state),out var count)?count:allJobs.Count(x=>S(x,"status")==state);
        activityRunning.Text=Count("running").ToString();activityPending.Text=Count("pending").ToString();activityFailed.Text=Count("failed").ToString();
        var jobs=allJobs.Where(x=>activityFilter.SelectedIndex switch{1=>S(x,"status") is "running" or "pending",2=>S(x,"status")=="failed",3=>S(x,"status")=="done",_=>true});
        FillActivity(jobsList,jobs,r=>[ActivityKind(S(r,"kind")),PipelineStatus(S(r,"status")),ActivityJobTitle(r),LocalTime(S(r,"updated"))]);
        FillActivity(movesList,activityData["operations"]!.AsArray().Where(x=>x!=null).Cast<JsonNode>(),r=>[LocalTime(S(r,"created")),OperationState(S(r,"state")),S(r,"new_path"),S(r,"reason")]);
        FillActivity(trashList,activityData["trash"]!.AsArray().Where(x=>x!=null).Cast<JsonNode>(),r=>[S(r,"title"),LocalTime(S(r,"deleted_at"))]);
        FillActivity(logList,activityData["activity"]!.AsArray().Where(x=>x!=null).Cast<JsonNode>(),r=>[LocalTime(S(r,"time")),ActivityKind(S(r,"kind")),S(r,"message")]);
        var current=activityTab switch{"moves"=>movesList,"trash"=>trashList,"log"=>logList,_=>jobsList};
        activityCount.Text=$"显示 {current.Items.Count} 条"+(activityTab=="trash"?"":activityTab=="jobs"?" · 最多 100 条，优先显示未完成任务":" · 最多 100 条");
    }
    void FillActivity(ListView view,IEnumerable<JsonNode> rows,Func<JsonNode,string[]> columns){
        string selected=view.SelectedItems.Count>0?S(view.SelectedItems[0].Tag as JsonNode,"id"):"",top=view.Items.Count>0?S(view.TopItem?.Tag as JsonNode,"id"):"";activityFilling.Add(view);view.BeginUpdate();
        try{view.Items.Clear();foreach(var r in rows){var item=new ListViewItem(columns(r)){Tag=r,ToolTipText=ActivityRecordText(view,r),UseItemStyleForSubItems=false};
            if(view==jobsList&&item.SubItems.Count>1)item.SubItems[1].ForeColor=S(r,"status") switch{"failed"=>Color.FromArgb(174,83,54),"running"=>Accent,_=>Muted};view.Items.Add(item);if(S(r,"id")==selected)item.Selected=true;
        }}finally{view.EndUpdate();activityFilling.Remove(view);}
        if(top!=""){var topItem=view.Items.Cast<ListViewItem>().FirstOrDefault(x=>S(x.Tag as JsonNode,"id")==top);if(topItem!=null)view.TopItem=topItem;}
        activityEmpty[view].Text=view==jobsList?(activityFilter.SelectedIndex==2?"当前记录中没有失败任务\n处理异常会显示在这里。":"暂无任务\n扫描或导入文档后，处理进度会出现在这里。"):view==trashList?"回收站为空\n删除的文档会保留在这里，方便恢复。":view==movesList?"暂无归档变更\n秘书整理文件后，可在这里查看并撤销变更。":"暂无活动日志";
        activityEmpty[view].Visible=view.Items.Count==0;view.Visible=view.Items.Count>0;SizeActivityColumns(view);UpdateActivitySelection(view);
    }
    static string ActivityJobTitle(JsonNode r)=>S(r,"document_title")!=""?S(r,"document_title"):S(r,"kind") switch{"chat"=>"文档秘书对话","review"=>"整个文档库",_=>"文档处理任务"};
    static string ActivityKind(string kind)=>kind switch{"format_summary"=>"Markdown 整理","index"=>"内容分析","reanalyze"=>"重新分析","organize"=>"整理归档","chat"=>"秘书对话","review"=>"文档库检查","reindex"=>"重建搜索索引","embeddings"=>"更新语义索引","analysis"=>"页面分析","summary"=>"概括整理","embedding"=>"语义索引","preview"=>"预览","error"=>"异常","agent"=>"秘书记录","agent_tool"=>"秘书操作","split"=>"文档拆分","merge"=>"跨扫描合并","reconcile"=>"来源核验","scan"=>"扫描","import"=>"导入","archive"=>"归档","trash"=>"回收站","restore"=>"文档恢复","undo"=>"撤销",_=>kind};
    static string OperationState(string state)=>state switch{"applied"=>"已完成","undone"=>"已撤销","planned" or "prepared"=>"准备中","undoing"=>"正在撤销","failed"=>"失败",_=>state};
    string ActivityRecordText(ListView view,JsonNode row){
        if(view==jobsList){
            string state=S(row,"status"),explanation=state switch{"running"=>"正在后台处理，你可以继续扫描和浏览文档。","pending"=>"任务已加入队列，会按处理能力自动开始。","done"=>"处理已完成。","failed"=>"任务未完成。查看以下原因后，可重试此任务。","cancelled"=>"任务已取消。",_=>""};
            return $"{ActivityKind(S(row,"kind"))} · {PipelineStatus(state)}\r\n{ActivityJobTitle(row)}\r\n更新时间：{LocalTime(S(row,"updated"))}\r\n\r\n{explanation}"+(S(row,"error")==""?"":"\r\n\r\n原因："+S(row,"error"));
        }
        if(view==movesList){
            if(S(row,"kind")=="merge")return $"跨扫描合并 · {OperationState(S(row,"state"))} · {LocalTime(S(row,"created"))}\r\n生成文件：{S(row,"new_path")}\r\n\r\n合并依据：{S(row,"reason")}\r\n\r\n撤销后，合并结果移入应用回收站，原始来源重新显示；扫描原件始终保留。";
            return $"{OperationState(S(row,"state"))} · {LocalTime(S(row,"created"))}\r\n原位置：{S(row,"old_path")}\r\n新位置：{S(row,"new_path")}\r\n\r\n{S(row,"reason")}";
        }
        if(view==trashList)return $"{S(row,"title")}\r\n删除时间：{LocalTime(S(row,"deleted_at"))}\r\n\r\n恢复后会重新显示在桌面和网页的文档库中。";
        return $"{ActivityKind(S(row,"kind"))} · {LocalTime(S(row,"time"))}\r\n\r\n{S(row,"message")}";
    }
    void UpdateActivitySelection(ListView view){
        if(activityFilling.Contains(view))return;
        var row=view.SelectedItems.Count>0?view.SelectedItems[0].Tag as JsonNode:null;
        if(activityDetails.TryGetValue(view,out var details)){string text=row==null?"选择上方记录，查看完整信息。":ActivityRecordText(view,row);if(details.Text!=text)details.Text=text;}
        if(activityActions.TryGetValue(view,out var button))button.Enabled=!activityActionBusy.Contains(view)&&row!=null&&(view==jobsList?S(row,"status")=="failed":view!=movesList||S(row,"state")=="applied");
    }
    Button ActivityCommandButton(string text,ListView view,string command){
        var button=PrimaryButton(text,146,34);button.BackColor=Color.White;button.ForeColor=Ink;button.FlatAppearance.BorderColor=Line;button.FlatAppearance.BorderSize=1;
        button.Click+=async(_,_)=>{activityActionBusy.Add(view);button.Enabled=false;try{await RunUi(()=>ActivityAction(view,command));}finally{activityActionBusy.Remove(view);if(!button.IsDisposed)UpdateActivitySelection(view);}};return button;
    }
    async Task ActivityAction(ListView view,string command)
    {
        if(demo){status.Text="演示模式不会更改真实文档。";return;}
        if(view.SelectedItems.Count==0){status.Text="请先选择一条记录。";return;}var row=(JsonNode)view.SelectedItems[0].Tag!;
        if(command=="retry_job"&&S(row,"status")!="failed")throw new InvalidOperationException("只能重试失败任务；排队中的任务会自动执行。");
        if(command=="undo"&&S(row,"state")!="applied")throw new InvalidOperationException("只能撤销尚未撤销的归档操作。");
        await SecretaryIntegration.Command(command,new(){["id"]=S(row,"id")});status.Text=command switch{"retry_job"=>"任务已重新加入队列。","restore"=>"文档已恢复，桌面与网页同步更新。",_=>S(row,"kind")=="merge"?"合并已撤销，原始来源已恢复。":"归档变更已撤销，文件已恢复原位置。"};await RefreshActivity();
    }
    // Synthetic data only; invoked by --ui-smoke without the live core or API.
    void LoadActivityDemo(){
        activityData=new JsonObject{
            ["jobs"]=new JsonArray(new JsonObject{["id"]="a",["kind"]="index",["status"]="running",["document_title"]="家庭设备保修凭证",["updated"]="2026-09-29T15:24:00-04:00"},new JsonObject{["id"]="b",["kind"]="organize",["status"]="pending",["document_title"]="物理课堂笔记",["updated"]="2026-09-29T15:22:00-04:00"},new JsonObject{["id"]="c",["kind"]="index",["status"]="failed",["document_title"]="家庭资料清单",["updated"]="2026-09-29T15:20:00-04:00",["error"]="演示：连接暂时中断。已完成的页面保留，可重试继续。"}),
            ["operations"]=new JsonArray(),["trash"]=new JsonArray(),["activity"]=new JsonArray(new JsonObject{["id"]=1,["kind"]="split",["time"]="2026-09-29T15:18:00-04:00",["message"]="示例扫描已按内容拆分为独立文档。"})};RenderActivity();
        if(jobsList.Items.Count>0)jobsList.Items[0].Selected=true;
    }
}
