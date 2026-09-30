using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly ListView jobsList=ActivityList(),movesList=ActivityList(),trashList=ActivityList(),logList=ActivityList();
    string activitySignature="";
    static ListView ActivityList()=>new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BorderStyle=BorderStyle.None,ShowItemToolTips=true};
    async Task ShowActivity()
    {
        if(activityPage==null){
            var page=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(16)};
            var commands=new FlowLayoutPanel{Dock=DockStyle.Top,Height=48};
            commands.Controls.AddRange([ActionButton("刷新记录",RefreshActivity,this,100),ActionButton("立即唤醒秘书",async()=>{await SecretaryIntegration.Command("maintenance");status.Text="全库检查已加入队列";await RefreshActivity();},this,145)]);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            void Add(string title,ListView list,string[] columns,Control[] actions){
                var tab=new TabPage(title){Padding=new Padding(12),BackColor=Color.White};foreach(string column in columns)list.Columns.Add(column,column=="说明"||column=="文档 / 内容"?410:150);
                var bar=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=46,Padding=new Padding(0,8,0,0)};bar.Controls.AddRange(actions);tab.Controls.Add(list);tab.Controls.Add(bar);list.BringToFront();tabs.TabPages.Add(tab);
                list.DoubleClick+=(_,_)=>{if(list.SelectedItems.Count>0)MessageBox.Show(this,string.Join("\n",list.SelectedItems[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x=>x.Text))+"\n\n"+list.SelectedItems[0].ToolTipText,title);};
            }
            Add("任务队列",jobsList,["任务","状态","文档 / 内容","更新时间"],[ActionButton("重试失败任务",()=>ActivityAction(jobsList,"retry_job"),this,140)]);
            Add("归档变更",movesList,["时间","状态","文档 / 内容","说明"],[ActionButton("撤销选中变更",()=>ActivityAction(movesList,"undo"),this,140)]);
            Add("回收站",trashList,["文档 / 内容","删除时间"],[ActionButton("恢复选中文档",()=>ActivityAction(trashList,"restore"),this,140)]);
            Add("活动日志",logList,["时间","类型","说明"],[]);
            page.Controls.Add(tabs);page.Controls.Add(commands);tabs.BringToFront();activityPage=page;
        }
        SwitchView(activityPage!,"activity");await RefreshActivity();
    }
    async Task RefreshActivity()
    {
        if(demo||activityPage==null)return;
        var data=await SecretaryIntegration.Command("activity",ct:lifetime.Token);if(IsDisposed)return;
        string signature=data.ToJsonString();if(signature==activitySignature)return;activitySignature=signature;
        static void Fill(ListView view,JsonArray rows,Func<JsonNode,string[]> columns){
            string selected=view.SelectedItems.Count>0?S(view.SelectedItems[0].Tag as JsonNode,"id"):"";view.BeginUpdate();
            try{view.Items.Clear();foreach(var r in rows){if(r==null)continue;var item=new ListViewItem(columns(r)){Tag=r,ToolTipText=S(r,"error")};view.Items.Add(item);if(S(r,"id")==selected)item.Selected=true;}}finally{view.EndUpdate();}
        }
        Fill(jobsList,data["jobs"]!.AsArray(),r=>[S(r,"kind") switch{"index"=>"内容分析","organize"=>"文档整理","chat"=>"秘书对话","review"=>"全库检查","reindex"=>"重建索引","embeddings"=>"修复向量",_=>S(r,"kind")},PipelineStatus(S(r,"status")),S(r,"document_title")==""?S(r,"error"):S(r,"document_title"),LocalTime(S(r,"updated"))]);
        Fill(movesList,data["operations"]!.AsArray(),r=>[LocalTime(S(r,"created")),S(r,"state"),S(r,"new_path"),S(r,"reason")]);
        Fill(trashList,data["trash"]!.AsArray(),r=>[S(r,"title"),LocalTime(S(r,"deleted_at"))]);
        Fill(logList,data["activity"]!.AsArray(),r=>[LocalTime(S(r,"time")),S(r,"kind"),S(r,"message")]);
    }
    async Task ActivityAction(ListView view,string command)
    {
        if(view.SelectedItems.Count==0){status.Text="请先选择一条记录。";return;}var row=(JsonNode)view.SelectedItems[0].Tag!;
        if(command=="retry_job"&&S(row,"status")!="failed")throw new InvalidOperationException("只能重试失败任务；排队中的任务会自动执行。");
        if(command=="undo"&&S(row,"state")!="applied")throw new InvalidOperationException("只能撤销尚未撤销的归档操作。");
        await SecretaryIntegration.Command(command,new(){["id"]=S(row,"id")});status.Text="操作完成，桌面与网页已同步更新。";await RefreshActivity();
    }
}
