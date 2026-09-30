using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly ComboBox conversations=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,IntegralHeight=false,DropDownHeight=300,AccessibleName="对话记录"};
    readonly MarkdownView transcript=new(){AccessibleName="秘书对话内容"};
    readonly TextBox messageBox=new(){Dock=DockStyle.Fill,Multiline=true,AcceptsReturn=true,ScrollBars=ScrollBars.Vertical,BorderStyle=BorderStyle.None,PlaceholderText="描述要找的资料，或提出整理要求…",AccessibleName="给文档秘书的消息"};
    readonly CheckBox attachDocument=new(){Text="关联当前文档",AutoSize=true,Margin=new Padding(0,5,0,0)};
    readonly Label contextLabel=new(){Dock=DockStyle.Fill,ForeColor=Accent,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(8,0,4,0)};
    readonly Label agentStatus=new(){Dock=DockStyle.Fill,Text="检索原文 · 核对信息 · 整理归档",ForeColor=Muted,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
    readonly Button sendMessage=PrimaryButton("发送",80,34);
    readonly Panel citations=new(){Dock=DockStyle.Bottom,Height=40,Padding=new Padding(0,4,0,4),Visible=false};
    readonly ComboBox citationPicker=new(){Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,AccessibleName="回答引用的原文页面"};
    readonly Panel attachedContext=new(){Dock=DockStyle.Top,Height=28,BackColor=Surface,Visible=false,Margin=Padding.Empty};
    readonly Button latestReply=new(){Text="↓ 查看最新消息",Dock=DockStyle.Bottom,Height=30,Visible=false,FlatStyle=FlatStyle.Flat,FlatAppearance={BorderSize=0},BackColor=Surface,ForeColor=Accent,Cursor=Cursors.Hand};
    readonly Dictionary<string,string> chatDrafts=[];
    readonly List<ChatReference> chatReferences=[];
    readonly List<(int start,int length,ChatReference reference)> citationRanges=[];
    string conversationId="",chatSignature="",historySignature="";
    int conversationVersion;
    bool updatingConversations,pollingChat,sending,chatBusy,refreshChatRequested;
    sealed record ConversationItem(string Id,string Title){public override string ToString()=>Title;}
    internal sealed record ChatReference(string DocumentId,int Page,int Number)
    {
        public string Label=>$"原文 {Number} · 第 {Page} 页";
        public override string ToString()=>Label;
    }

    TabPage BuildSecretary()
    {
        var tab=new TabPage("文档秘书"){BackColor=Color.White,Padding=new Padding(12)};
        var top=new TableLayoutPanel{Dock=DockStyle.Top,Height=40,ColumnCount=2,RowCount=1,Margin=Padding.Empty};
        top.ColumnStyles.Add(new(SizeType.Percent,100));top.ColumnStyles.Add(new(SizeType.Absolute,82));
        conversations.Margin=new Padding(0,3,8,0);
        var fresh=ActionButton("新对话",()=>{ChangeConversation("",true);return Task.CompletedTask;},this,76);fresh.Margin=Padding.Empty;
        top.Controls.Add(conversations,0,0);top.Controls.Add(fresh,1,0);
        conversations.Items.Add(new ConversationItem("","新对话"));conversations.SelectedIndex=0;
        conversations.SelectedIndexChanged+=(_,_)=>{if(!updatingConversations)ChangeConversation((conversations.SelectedItem as ConversationItem)?.Id??"");};

        var statusRow=new Panel{Dock=DockStyle.Top,Height=35,Padding=new Padding(0,0,0,5)};statusRow.Controls.Add(agentStatus);
        var compose=new Panel{Dock=DockStyle.Bottom,Height=178,Padding=new Padding(0,10,0,0)};
        var bottom=new Panel{Dock=DockStyle.Bottom,Height=40,Padding=new Padding(0,6,0,0)};
        var keys=new Label{Text="Ctrl + Enter 发送",Dock=DockStyle.Fill,ForeColor=Muted,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true};
        sendMessage.Dock=DockStyle.Right;bottom.Controls.Add(keys);bottom.Controls.Add(sendMessage);keys.BringToFront();
        var editor=new Panel{Dock=DockStyle.Fill,Padding=new Padding(10),BackColor=Color.White};
        editor.Paint+=(_,e)=>{using var pen=new Pen(messageBox.Focused?Accent:Line);e.Graphics.DrawRectangle(pen,0,0,editor.Width-1,editor.Height-1);};
        messageBox.GotFocus+=(_,_)=>editor.Invalidate();messageBox.LostFocus+=(_,_)=>editor.Invalidate();editor.Controls.Add(messageBox);
        var attachmentRow=new FlowLayoutPanel{Dock=DockStyle.Top,Height=30,WrapContents=false,Margin=Padding.Empty};attachmentRow.Controls.Add(attachDocument);
        attachedContext.Controls.Add(contextLabel);
        compose.Controls.Add(editor);compose.Controls.Add(bottom);compose.Controls.Add(attachedContext);compose.Controls.Add(attachmentRow);editor.BringToFront();

        var citeButton=ActionButton("查看原文",async()=>{if(citationPicker.SelectedItem is ChatReference reference)await OpenCitation(reference.DocumentId,reference.Page);},this,84);citeButton.Dock=DockStyle.Right;citeButton.Height=30;
        var citationHost=new Panel{Dock=DockStyle.Fill,Padding=new Padding(0,2,8,0)};citationHost.Controls.Add(citationPicker);
        citations.Controls.Add(citationHost);citations.Controls.Add(citeButton);citationHost.BringToFront();
        var reader=new Panel{Dock=DockStyle.Fill,Padding=new Padding(0,4,0,0)};
        reader.Controls.Add(transcript);reader.Controls.Add(latestReply);reader.Controls.Add(citations);transcript.BringToFront();
        tab.Controls.Add(reader);tab.Controls.Add(compose);tab.Controls.Add(statusRow);tab.Controls.Add(top);reader.BringToFront();

        sendMessage.Click+=async(_,_)=>await RunUi(SendMessage);
        messageBox.KeyDown+=async(_,e)=>{if(e.Control&&e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await RunUi(SendMessage);}};
        messageBox.TextChanged+=(_,_)=>UpdateSendState();
        attachDocument.CheckedChanged+=(_,_)=>{UpdateChatContext();compose.Height=attachDocument.Checked?202:174;};
        latestReply.Click+=(_,_)=>{transcript.ScrollToEnd();latestReply.Visible=false;};
        transcript.VScroll+=(_,_)=>{if(transcript.IsAtEnd)latestReply.Visible=false;};
        transcript.MouseMove+=(_,e)=>transcript.Cursor=ReferenceAt(e.Location)!=null?Cursors.Hand:Cursors.IBeam;
        transcript.MouseClick+=async(_,e)=>{if(e.Button==MouseButtons.Left&&transcript.SelectionLength==0&&ReferenceAt(e.Location) is { } reference)await RunUi(()=>OpenCitation(reference.DocumentId,reference.Page));};
        ShowChatWelcome();UpdateChatContext();UpdateSendState();
        return tab;
    }

    void UpdateChatContext()
    {
        attachDocument.Enabled=selectedDocument!=null||attachDocument.Checked;
        attachedContext.Visible=attachDocument.Checked;
        contextLabel.Text=selectedDocument==null?"请选择要关联的文档":$"{S(selectedDocument,"title")} · 第 {previewPage+1} 页";
        contextLabel.AccessibleDescription=contextLabel.Text;
    }
    void UpdateSendState()
    {
        sendMessage.Enabled=!sending&&!chatBusy&&messageBox.Text.Trim().Length>0;
        sendMessage.Text=sending?"发送中…":chatBusy?"等待回复":"发送";
    }
    void ShowChatWelcome()
    {
        transcript.SetMarkdown("## 资料很多，也能找到。\n\n告诉我你记得的内容、时间或线索，我会查找文档，并把原文带到你面前。\n\n### 你可以这样问\n\n- 找一下上周扫描的保修单\n- 这份讲义有哪些重点？\n- 帮我检查有没有需要整理的文档\n\n关联当前文档后，可以围绕正在预览的页面提问。",true);
        ClearCitations();latestReply.Visible=false;
    }
    void ClearCitations(){chatReferences.Clear();citationRanges.Clear();citationPicker.Items.Clear();citations.Visible=false;}
    void ChangeConversation(string id,bool fresh=false)
    {
        chatDrafts[conversationId]=messageBox.Text;
        conversationVersion++;conversationId=id;chatSignature="";chatBusy=false;
        messageBox.Text=fresh?"":chatDrafts.GetValueOrDefault(id,"");
        updatingConversations=true;
        try{conversations.SelectedItem=conversations.Items.Cast<ConversationItem>().FirstOrDefault(item=>item.Id==id)??conversations.Items[0];}
        finally{updatingConversations=false;}
        ClearCitations();latestReply.Visible=false;
        if(id==""){ShowChatWelcome();agentStatus.Text="检索原文 · 核对信息 · 整理归档";}
        else{transcript.SetMarkdown("正在载入这段对话…",true);agentStatus.Text="正在读取对话记录…";chatBusy=true;}
        UpdateSendState();messageBox.Focus();
        if(id!="")_ = RunUi(RefreshConversation);
    }
    void AskAboutSelected(){ShowHome();if(focusReading)ToggleFocusReading();inspector.SelectedIndex=1;attachDocument.Checked=selectedDocument!=null;UpdateChatContext();messageBox.Focus();}
    async Task SendMessage()
    {
        if(sending||chatBusy||demo)return;
        string text=messageBox.Text.Trim();if(text==""){messageBox.Focus();return;}
        string submitted=text;
        if(attachDocument.Checked){if(selectedDocument==null)throw new ArgumentException("请先选择一份文档，或取消关联当前文档。");submitted+=$"\n\n参考文档：{S(selectedDocument,"title")}\n[document_id={selectedId}; page={previewPage+1}]";}
        int version=conversationVersion;string targetId=conversationId;bool accepted=false;
        sending=true;UpdateSendState();agentStatus.Text="正在发送…";
        try{
            var result=await SecretaryIntegration.Command("chat",new(){["id"]=targetId,["message"]=submitted},lifetime.Token);
            accepted=true;
            if(IsDisposed)return;
            string receivedId=S(result,"conversation");
            if(chatDrafts.TryGetValue(targetId,out string? savedDraft)){
                if(savedDraft.Trim()==text)chatDrafts.Remove(targetId);
                else if(targetId==""&&receivedId!=""){chatDrafts[receivedId]=savedDraft;chatDrafts.Remove(targetId);}
            }
            if(version!=conversationVersion){historySignature="";return;}
            version=++conversationVersion;
            conversationId=S(result,"conversation");
            // Keep a follow-up draft typed while the send request was in flight.
            if(messageBox.Text.Trim()==text)messageBox.Clear();
            chatSignature="";chatBusy=true;agentStatus.Text="已收到 · 秘书正在准备处理";
            await RefreshConversation();
        }catch{
            if(accepted){if(version==conversationVersion)agentStatus.Text="已发送 · 正在重新连接以接收回复";return;}
            if(version==conversationVersion){chatBusy=false;agentStatus.Text="发送未完成 · 内容已保留，可再次发送";}
            throw;
        }
        finally{sending=false;if(!IsDisposed)UpdateSendState();}
    }
    async Task RefreshConversation()
    {
        if(demo)return;
        if(pollingChat){refreshChatRequested=true;return;}
        pollingChat=true;
        try{
            var history=await SecretaryIntegration.Command("conversations",ct:lifetime.Token);
            if(IsDisposed)return;
            string signature=history.ToJsonString();
            if(signature!=historySignature){
                historySignature=signature;updatingConversations=true;
                try{
                    conversations.Items.Clear();conversations.Items.Add(new ConversationItem("","新对话"));
                    foreach(var item in history["conversations"]!.AsArray())conversations.Items.Add(new ConversationItem(S(item,"id"),S(item,"title")));
                    conversations.SelectedItem=conversations.Items.Cast<ConversationItem>().FirstOrDefault(x=>x.Id==conversationId)??conversations.Items[0];
                }finally{updatingConversations=false;}
            }
            string id=conversationId;int version=conversationVersion;
            if(id==""){chatBusy=false;UpdateSendState();return;}
            var data=await SecretaryIntegration.Command("conversation",new(){["id"]=id},lifetime.Token);
            if(IsDisposed||id!=conversationId||version!=conversationVersion)return;
            var job=data["jobs"]!.AsArray().FirstOrDefault();string state=S(job,"status");
            chatBusy=state is "pending" or "running";
            agentStatus.Text=state switch{"pending"=>"已收到 · 正在排队，可继续浏览文档","running"=>"正在检索、阅读和整理…","failed"=>"回复未完成 · 可到处理记录重试",_=>"回答已更新 · 点击引用核对原文"};
            agentStatus.ForeColor=state=="failed"?Color.FromArgb(165,83,47):Muted;
            UpdateSendState();
            var messages=data["messages"]!.AsArray();signature=messages.ToJsonString();
            if(signature==chatSignature)return;
            bool first=chatSignature=="";chatSignature=signature;RenderChatMessages(messages,first);
        }finally{
            pollingChat=false;
            if(refreshChatRequested&&!IsDisposed&&IsHandleCreated){refreshChatRequested=false;BeginInvoke((Action)(async()=>await RunUi(RefreshConversation)));}
        }
    }
    void RenderChatMessages(JsonArray messages,bool first=false)
    {
        bool follow=first||(transcript.SelectionLength==0&&transcript.IsAtEnd);
        var formatted=FormatChatMessages(messages);
        transcript.SetMarkdown(formatted.Markdown,first);
        ClearCitations();chatReferences.AddRange(formatted.References);
        citationPicker.Items.AddRange(chatReferences.Cast<object>().ToArray());
        if(citationPicker.Items.Count>0)citationPicker.SelectedIndex=0;
        citations.Visible=chatReferences.Count>0;
        RebuildCitationRanges();
        if(follow){transcript.ScrollToEnd();latestReply.Visible=false;}else latestReply.Visible=true;
    }
    internal static (string Markdown,List<ChatReference> References) FormatChatMessages(JsonArray messages)
    {
        var markdown=new StringBuilder();var references=new List<ChatReference>();
        foreach(var message in messages){
            bool user=S(message,"role")=="user";
            string when=DateTimeOffset.TryParse(S(message,"created"),out var time)?" · "+time.ToLocalTime().ToString("HH:mm"):"";
            if(markdown.Length>0)markdown.AppendLine("\n---\n");
            markdown.Append("### ").Append(user?"你":"文档秘书").AppendLine(when).AppendLine();
            string body=Regex.Replace(S(message,"text"),@"\[document_id=[a-fA-F0-9]+; page=\d+\]","").Trim();
            if(user){
                // User messages remain literal, even when a search term contains Markdown.
                body=Regex.Replace(body,@"([\\`*_{}\[\]()#+.!|>~-])",@"\$1");
                markdown.AppendLine(string.Join("\n",body.Replace("\r","").Split('\n').Select(line=>"> "+line)));
            }else{
                // Some replies surround bold labels with spaces. Normalize those delimiters only.
                body=Regex.Replace(body,@"\*\*([^*\r\n]+)\*\*",match=>"**"+match.Groups[1].Value.Trim()+"**");
                body=Regex.Replace(body,@"\[\[([a-fA-F0-9]{32}):(\d+)\]\]",match=>{
                    if(!int.TryParse(match.Groups[2].Value,out int page)||page<1)return match.Value;
                    string id=match.Groups[1].Value.ToLowerInvariant();
                    var reference=references.FirstOrDefault(item=>item.DocumentId.Equals(id,StringComparison.OrdinalIgnoreCase)&&item.Page==page);
                    if(reference==null){reference=new ChatReference(id,page,references.Count+1);references.Add(reference);}
                    return $"[{reference.Label}](scanarchive-reference:{reference.Number})";
                });
                markdown.AppendLine(body);
            }
        }
        return (markdown.ToString(),references);
    }
    void RebuildCitationRanges()
    {
        citationRanges.Clear();
        foreach(var reference in chatReferences){
            int from=0;while(from<transcript.TextLength){int start=transcript.Text.IndexOf(reference.Label,from,StringComparison.Ordinal);if(start<0)break;citationRanges.Add((start,reference.Label.Length,reference));from=start+reference.Label.Length;}
        }
    }
    ChatReference? ReferenceAt(Point point)
    {
        int character=transcript.GetCharIndexFromPosition(point);
        var position=transcript.GetPositionFromCharIndex(character);
        if(point.X<position.X-2||point.X>position.X+24||Math.Abs(point.Y-position.Y)>transcript.Font.Height+4)return null;
        return citationRanges.FirstOrDefault(item=>character>=item.start&&character<item.start+item.length).reference;
    }
    async Task OpenCitation(string id,int page)
    {
        ShowHome();if(focusReading)ToggleFocusReading();inspector.SelectedIndex=1;await SelectDocument(new JsonObject{["doc_id"]=id,["page"]=page});
        if(selectedId==id&&S(selectedDocument,"id")==id)status.Text=$"已定位引用 · 第 {previewPage+1} 页";
    }
}
