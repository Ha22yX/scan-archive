using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly ComboBox conversations=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList};
    readonly RichTextBox transcript=new(){Dock=DockStyle.Fill,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Color.White,DetectUrls=false};
    readonly TextBox messageBox=new(){Dock=DockStyle.Fill,Multiline=true,AcceptsReturn=true,ScrollBars=ScrollBars.Vertical,PlaceholderText="描述你要找的文件，或让秘书帮你整理…"};
    readonly CheckBox attachDocument=new(){Text="附带当前文档与页码",Dock=DockStyle.Top,Height=28,AutoSize=false};
    readonly Label contextLabel=new(){Text="可直接提问，也可附带选中的文档",Dock=DockStyle.Top,Height=38,ForeColor=Muted,AutoEllipsis=true};
    readonly Label agentStatus=new(){Dock=DockStyle.Top,Height=32,Text="秘书可帮你查找、核对和整理文档",ForeColor=Muted};
    readonly Button sendMessage=PrimaryButton("发送",76,34);
    readonly FlowLayoutPanel citations=new(){Dock=DockStyle.Bottom,Height=76,AutoScroll=true,WrapContents=true};
    string conversationId="",chatSignature="",historySignature="";
    bool updatingConversations,pollingChat,sending;
    sealed record ConversationItem(string Id,string Title){public override string ToString()=>Title;}

    TabPage BuildSecretary()
    {
        var tab=new TabPage("文档秘书"){BackColor=Color.White,Padding=new Padding(10)};
        var top=new Panel{Dock=DockStyle.Top,Height=40};var fresh=ActionButton("新对话",()=>{conversationId="";chatSignature="";transcript.Clear();citations.Controls.Clear();agentStatus.Text="开始一个新对话";sendMessage.Enabled=true;updatingConversations=true;conversations.SelectedIndex=0;updatingConversations=false;messageBox.Focus();return Task.CompletedTask;},this,76);fresh.Dock=DockStyle.Right;
        top.Controls.Add(conversations);top.Controls.Add(fresh);conversations.BringToFront();
        conversations.Items.Add(new ConversationItem("","新对话"));conversations.SelectedIndex=0;
        conversations.SelectedIndexChanged+=async(_,_)=>{if(updatingConversations)return;conversationId=(conversations.SelectedItem as ConversationItem)?.Id??"";chatSignature="";await RunUi(RefreshConversation);};
        var compose=new Panel{Dock=DockStyle.Bottom,Height=186};var bottom=new Panel{Dock=DockStyle.Bottom,Height=40,Padding=new Padding(0,6,0,0)};
        var keys=new Label{Text="Ctrl + Enter 发送",Dock=DockStyle.Fill,ForeColor=Muted,TextAlign=ContentAlignment.MiddleLeft};sendMessage.Dock=DockStyle.Right;bottom.Controls.Add(keys);bottom.Controls.Add(sendMessage);keys.BringToFront();
        compose.Controls.Add(messageBox);compose.Controls.Add(bottom);compose.Controls.Add(contextLabel);compose.Controls.Add(attachDocument);messageBox.BringToFront();
        tab.Controls.Add(transcript);tab.Controls.Add(citations);tab.Controls.Add(compose);tab.Controls.Add(agentStatus);tab.Controls.Add(top);transcript.BringToFront();
        sendMessage.Click+=async(_,_)=>await RunUi(SendMessage);
        messageBox.KeyDown+=async(_,e)=>{if(e.Control&&e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await RunUi(SendMessage);}};
        attachDocument.CheckedChanged+=(_,_)=>contextLabel.Text=attachDocument.Checked?(selectedDocument==null?"请先选择文档":"将附带："+S(selectedDocument,"title")):"当前问题不附带文档";
        transcript.Text="你好，我是你的文档秘书。\n\n试着问我：\n• 找一下上周扫描的保修单\n• 这份讲义的重点是什么？\n• 帮我检查有没有需要整理的文档\n\n回答中的原文引用可以直接在左侧预览。";
        return tab;
    }
    void AskAboutSelected(){ShowHome();inspector.SelectedIndex=1;attachDocument.Checked=selectedDocument!=null;messageBox.Focus();}
    async Task SendMessage()
    {
        if(sending||!sendMessage.Enabled||demo)return;
        string text=messageBox.Text.Trim();if(text==""){messageBox.Focus();return;}
        string submitted=text;
        if(attachDocument.Checked){if(selectedDocument==null)throw new ArgumentException("请先选择一份文档，或取消附带当前文档。");submitted+=$"\n\n参考文档：{S(selectedDocument,"title")}\n[document_id={selectedId}; page={previewPage+1}]";}
        sending=true;sendMessage.Enabled=false;
        try{
            var result=await SecretaryIntegration.Command("chat",new(){["id"]=conversationId,["message"]=submitted},lifetime.Token);
            conversationId=S(result,"conversation");messageBox.Clear();chatSignature="";await RefreshConversation();
        }catch{sendMessage.Enabled=true;throw;}finally{sending=false;if(!IsDisposed&&agentStatus.Text.StartsWith("秘书可"))sendMessage.Enabled=true;}
    }
    async Task RefreshConversation()
    {
        if(demo||pollingChat)return;pollingChat=true;
        try{
            var history=await SecretaryIntegration.Command("conversations",ct:lifetime.Token);
            if(IsDisposed)return;
            string signature=history.ToJsonString();if(signature!=historySignature){historySignature=signature;updatingConversations=true;
                conversations.Items.Clear();conversations.Items.Add(new ConversationItem("","新对话"));
                foreach(var item in history["conversations"]!.AsArray())conversations.Items.Add(new ConversationItem(S(item,"id"),S(item,"title")));
                conversations.SelectedItem=conversations.Items.Cast<ConversationItem>().FirstOrDefault(x=>x.Id==conversationId)??conversations.Items[0];updatingConversations=false;
            }
            string id=conversationId;if(id==""){sendMessage.Enabled=!sending;return;}
            var data=await SecretaryIntegration.Command("conversation",new(){["id"]=id},lifetime.Token);
            if(IsDisposed||id!=conversationId)return;
            var job=data["jobs"]!.AsArray().FirstOrDefault();string state=S(job,"status");
            agentStatus.Text=state switch{"pending"=>"已收到 · 等待当前后台任务完成","running"=>"秘书正在检索、阅读和处理…","failed"=>"本次未完成 · 可在处理记录中重试",_=>"秘书已回复 · 点击原文引用查看证据"};
            sendMessage.Enabled=!sending&&state is not ("pending" or "running");
            signature=data["messages"]!.ToJsonString();if(signature==chatSignature)return;chatSignature=signature;
            transcript.Clear();foreach(Control c in citations.Controls.Cast<Control>().ToArray())c.Dispose();citations.Controls.Clear();
            int index=0;
            foreach(var message in data["messages"]!.AsArray()){
                transcript.SelectionColor=S(message,"role")=="user"?Accent:Ink;transcript.SelectionFont=new Font(Font,FontStyle.Bold);transcript.AppendText(S(message,"role")=="user"?"你\n":"文档秘书\n");transcript.SelectionFont=Font;
                string body=Regex.Replace(S(message,"text"),@"\[document_id=[a-fA-F0-9]+; page=\d+\]","");
                body=Regex.Replace(body,@"\[\[([a-fA-F0-9]{32}):(\d+)\]\]",match=>{
                    string doc=match.Groups[1].Value;int page=int.Parse(match.Groups[2].Value);int n=++index;
                    var link=ActionButton($"原文 {n} · 第 {page} 页",()=>OpenCitation(doc,page),this,144);citations.Controls.Add(link);return $"[原文 {n} · 第 {page} 页]";
                });
                transcript.AppendText(body+"\n\n");
            }
            transcript.SelectionStart=transcript.TextLength;transcript.ScrollToCaret();
        }finally{pollingChat=false;}
    }
    async Task OpenCitation(string id,int page)
    {
        ShowHome();inspector.SelectedIndex=1;await SelectDocument(new JsonObject{["doc_id"]=id,["page"]=page});await RenderPage(page-1);
        status.Text=$"已定位引用 · 第 {page} 页";
    }
}
