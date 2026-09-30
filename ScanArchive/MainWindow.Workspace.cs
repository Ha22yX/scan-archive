using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly TextBox searchBox=new(){Dock=DockStyle.Fill,PlaceholderText="搜索内容、编号、姓名，或描述你要找的文件…",BorderStyle=BorderStyle.FixedSingle};
    readonly ComboBox categoryFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=160},stateFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=110};
    readonly ListView files=new(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BorderStyle=BorderStyle.None,ShowItemToolTips=true};
    readonly Label listTitle=Caption("文档库"),listHint=new(){Dock=DockStyle.Bottom,Height=44,ForeColor=Muted,Text="扫描或导入文件，即可建立文档库。",AutoEllipsis=true};
    readonly Label previewTitle=Caption("原件预览"),previewHint=new(){Text="选择一份文档，或开始扫描\n\n原始页面会一直为你保留",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Muted};
    readonly PictureBox preview=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Surface};
    readonly FlowLayoutPanel pdfNavigation=new(){Dock=DockStyle.Bottom,Height=43,WrapContents=false,Padding=new Padding(0,5,0,0)};
    readonly Button previousPage=new(){Text="‹",Width=36,Height=30},nextPage=new(){Text="›",Width=36,Height=30};
    readonly Label pageNumber=new(){Text="—",Width=64,Height=30,TextAlign=ContentAlignment.MiddleCenter};
    readonly NumericUpDown pageJump=new(){Minimum=1,Maximum=1,Width=55,Height=30};
    readonly MarkdownView summary=new();
    readonly Label documentFacts=new(){Dock=DockStyle.Top,Height=90,ForeColor=Muted,AutoEllipsis=true};
    readonly TabControl inspector=new(){Dock=DockStyle.Fill};
    readonly Button listPrevious=new(){Text="上一批",Width=72,Height=30},listNext=new(){Text="下一批",Width=72,Height=30};
    string searchQuery="",librarySignature="",selectedId="",previewPath="";
    int libraryOffset,libraryTotal,listVersion,selectionVersion,renderVersion,previewPage,previewPages=1;
    bool filterLoading,jumping;
    JsonObject? selectedDocument;
    JsonArray selectedPages=new();
    readonly SemaphoreSlim renderGate=new(1,1);

    void ShowHome()
    {
        if(homePage==null)BuildWorkspace();
        SwitchView(homePage!,"home");
    }
    void BuildWorkspace()
    {
        var page=new Panel{Dock=DockStyle.Fill};
        var toolbar=new TableLayoutPanel{Dock=DockStyle.Top,Height=48,ColumnCount=4,RowCount=1,Margin=Padding.Empty};
        toolbar.ColumnStyles.Add(new(SizeType.Percent,100));toolbar.ColumnStyles.Add(new(SizeType.Absolute,84));toolbar.ColumnStyles.Add(new(SizeType.Absolute,76));toolbar.ColumnStyles.Add(new(SizeType.Absolute,96));
        toolbar.Controls.Add(searchBox,0,0);searchBox.Margin=new Padding(0,4,8,0);
        toolbar.Controls.Add(ActionButton("搜索",SearchNow,this,78),1,0);
        toolbar.Controls.Add(ActionButton("清除",async()=>{searchBox.Clear();searchQuery="";libraryOffset=0;await RefreshFiles(true);},this,68),2,0);
        toolbar.Controls.Add(ActionButton("导入文件",ImportFiles,this,90),3,0);
        searchBox.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await RunUi(SearchNow);}};
        var grid=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=Padding.Empty};
        grid.ColumnStyles.Add(new(SizeType.Percent,26));grid.ColumnStyles.Add(new(SizeType.Percent,42));grid.ColumnStyles.Add(new(SizeType.Percent,32));grid.RowStyles.Add(new(SizeType.Percent,100));
        var library=new Panel{Dock=DockStyle.Fill};
        var filters=new FlowLayoutPanel{Dock=DockStyle.Top,Height=74,WrapContents=true};
        categoryFilter.Items.Add("全部分类");categoryFilter.SelectedIndex=0;
        stateFilter.Items.AddRange(["全部状态","等待分析","逐页分析","建立索引","等待整理","已归档","需要重试"]);stateFilter.SelectedIndex=0;
        filters.Controls.AddRange([categoryFilter,stateFilter]);
        async void Changed(object? sender,EventArgs e){if(filterLoading||demo)return;libraryOffset=0;await RunUi(async()=>{if(searchQuery!="")await SearchNow();else await RefreshFiles(true);});}
        categoryFilter.SelectedIndexChanged+=Changed;stateFilter.SelectedIndexChanged+=Changed;
        files.Columns.Add("文档",200);files.Columns.Add("状态 / 页",86);
        files.Resize+=(_,_)=>{if(files.Columns.Count>0)files.Columns[0].Width=Math.Max(90,files.ClientSize.Width-92);};
        files.SelectedIndexChanged+=async(_,_)=>{if(!refreshingFiles&&files.SelectedItems.Count>0)await RunUi(()=>SelectDocument((JsonObject)files.SelectedItems[0].Tag!));};
        files.DoubleClick+=async(_,_)=>await RunUi(()=>{OpenSelected();return Task.CompletedTask;});
        files.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Right&&files.GetItemAt(e.X,e.Y) is { } item)item.Selected=true;};
        files.ContextMenuStrip=FileMenu();
        var paging=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=36};paging.Controls.AddRange([listPrevious,listNext]);
        listPrevious.Click+=async(_,_)=>await RunUi(async()=>{libraryOffset=Math.Max(0,libraryOffset-60);await RefreshFiles(true);});
        listNext.Click+=async(_,_)=>await RunUi(async()=>{libraryOffset+=60;await RefreshFiles(true);});
        library.Controls.Add(files);library.Controls.Add(listHint);library.Controls.Add(paging);library.Controls.Add(filters);library.Controls.Add(listTitle);files.BringToFront();
        var libraryCard=Card("",library);libraryCard.Margin=new Padding(0,0,10,0);grid.Controls.Add(libraryCard,0,0);
        var viewer=new Panel{Dock=DockStyle.Fill};var canvas=new Panel{Dock=DockStyle.Fill,BackColor=Surface,Padding=new Padding(8)};
        canvas.Controls.Add(preview);canvas.Controls.Add(previewHint);previewHint.BringToFront();
        pdfNavigation.Controls.AddRange([previousPage,pageNumber,nextPage,pageJump]);
        previousPage.Click+=async(_,_)=>await RunUi(()=>ChangePdfPage(-1));nextPage.Click+=async(_,_)=>await RunUi(()=>ChangePdfPage(1));
        pageJump.ValueChanged+=async(_,_)=>{if(!jumping)await RunUi(()=>RenderPage((int)pageJump.Value-1));};
        var open=ActionButton("打开原件",()=>{OpenSelected();return Task.CompletedTask;},this,84);pdfNavigation.Controls.Add(open);
        viewer.Controls.Add(canvas);viewer.Controls.Add(pdfNavigation);viewer.Controls.Add(previewTitle);canvas.BringToFront();
        var previewCard=Card("",viewer);previewCard.Margin=new Padding(0,0,10,0);grid.Controls.Add(previewCard,1,0);
        var detailsTab=new TabPage("文档详情"){BackColor=Color.White,Padding=new Padding(10)};
        var tools=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=116,WrapContents=true};
        tools.Controls.AddRange([ActionButton("修改归档",EditSelected,this,96),ActionButton("询问秘书",()=>{AskAboutSelected();return Task.CompletedTask;},this,96),ActionButton("重新整理",()=>DocumentAction("organize"),this,96),ActionButton("Markdown 整理",()=>DocumentAction("format_summary"),this,135),ActionButton("分析重试",()=>DocumentAction("retry_analysis"),this,96)]);
        detailsTab.Controls.Add(summary);detailsTab.Controls.Add(tools);detailsTab.Controls.Add(documentFacts);summary.BringToFront();
        inspector.TabPages.Add(detailsTab);inspector.TabPages.Add(BuildSecretary());
        grid.Controls.Add(Card("",inspector),2,0);
        page.Controls.Add(grid);page.Controls.Add(toolbar);grid.BringToFront();homePage=page;
        summary.Text="选中文档后，这里会显示 AI 内容概括、关键词、来源与扫描时间。\n\n你也可以切换到文档秘书，直接描述想找的资料。";
    }
    string Category=>categoryFilter.SelectedIndex>0?categoryFilter.Text:"";
    string State=>stateFilter.SelectedIndex switch{1=>"queued",2=>"analyzing",3=>"indexing",4=>"analyzed",5=>"ready",6=>"error",_=>""};
    async Task RefreshFiles(bool force=false)
    {
        if(demo||searchQuery!="")return;
        stateFilter.Enabled=true;
        int version=++listVersion;
        var data=await SecretaryIntegration.Command("browse",new(){["offset"]=libraryOffset,["category"]=Category,["status"]=State},lifetime.Token);
        if(IsDisposed||version!=listVersion)return;
        libraryTotal=N(data,"total");var rows=data["documents"]!.AsArray();
        if(rows.Count==0&&libraryOffset>0){libraryOffset=Math.Max(0,libraryOffset-60);await RefreshFiles(true);return;}
        listTitle.Text=$"文档库 · {libraryTotal} 份";
        listHint.Text=$"{(libraryTotal==0?0:libraryOffset+1)}–{libraryOffset+rows.Count} / {libraryTotal}  ·  {N(data,"pending")} 个后台任务";
        if(!(data["configured"]?.GetValue<bool>()??false))listHint.Text="未配置 API Key · 扫描可保存，AI 任务等待配置";
        listPrevious.Enabled=libraryOffset>0;listNext.Enabled=libraryOffset+rows.Count<libraryTotal;
        string cats=Category;var choices=data["categories"]!.AsArray().Select(x=>S(x,"category")).ToArray();
        if(!categoryFilter.Items.Cast<string>().Skip(1).SequenceEqual(choices)){
            filterLoading=true;categoryFilter.Items.Clear();categoryFilter.Items.Add("全部分类");categoryFilter.Items.AddRange(choices);categoryFilter.SelectedIndex=Math.Max(0,categoryFilter.Items.IndexOf(cats));filterLoading=false;
        }
        string signature=rows.ToJsonString();if(!force&&signature==librarySignature)return;librarySignature=signature;
        PopulateFiles(rows,false);
        if(selectedId!=""&&rows.Any(x=>S(x,"id")==selectedId))await RefreshDetails(selectedId,selectionVersion);
    }
    void PopulateFiles(JsonArray rows,bool search)
    {
        string previous=selectedId;refreshingFiles=true;files.BeginUpdate();
        try{
            files.Items.Clear();
            foreach(var row in rows){if(row==null)continue;var d=row.AsObject();string id=S(d,search?"doc_id":"id");
                var item=new ListViewItem([S(d,"title"),search?$"第 {N(d,"page")} 页":PipelineStatus(S(d,"status"))]){Tag=d,ToolTipText=search?S(d,"snippet"):$"{S(d,"category")}\n扫描于 {LocalTime(S(d,"scanned"))}\n{S(d,"error")}"};
                item.ForeColor=S(d,"status")=="error"?Color.FromArgb(166,65,51):Ink;files.Items.Add(item);if(id==previous)item.Selected=true;
            }
        }finally{files.EndUpdate();refreshingFiles=false;}
        if(rows.Count==0)listHint.Text=search?"没有找到匹配内容，试试不同关键词或询问秘书。":"这里还没有文档。开始扫描，或导入已有文件。";
    }
    async Task SearchNow()
    {
        string query=searchBox.Text.Trim();searchQuery=query;libraryOffset=0;
        if(query==""){await RefreshFiles(true);return;}
        int version=++listVersion;listTitle.Text="正在搜索…";
        var result=await SecretaryIntegration.Command("search",new(){["q"]=query,["category"]=Category},lifetime.Token);
        if(IsDisposed||version!=listVersion)return;
        var rows=result["results"]!.AsArray();PopulateFiles(rows,true);listTitle.Text=$"匹配页面 · {rows.Count}";
        listHint.Text=(S(result,"mode")=="hybrid"?"语义 + 关键词检索":"关键词检索")+" · 点击结果定位原文页";
        if(rows.Count==0)listHint.Text="未找到匹配页面。可换个描述，或请秘书继续查找。";
        listPrevious.Enabled=listNext.Enabled=false;
        stateFilter.Enabled=false;
    }
    async Task SelectDocument(JsonObject row)
    {
        string id=S(row,"id");if(id=="")id=S(row,"doc_id");
        int targetPage=Math.Max(0,N(row,"page")-1);
        bool changed=id!=selectedId;if(changed){selectedDocument=null;summary.Clear();}selectedId=id;int version=++selectionVersion;
        var result=await SecretaryIntegration.Command("document",new(){["id"]=id},lifetime.Token);
        if(IsDisposed||version!=selectionVersion)return;
        ApplyDetails(result);previewTitle.Text=S(selectedDocument,"title");
        if(changed||searchQuery!=""){previewPath=S(selectedDocument,"original");previewPages=Math.Max(1,N(selectedDocument,"page_count"));await RenderPage(targetPage);}
    }
    async Task RefreshDetails(string id,int version)
    {
        var result=await SecretaryIntegration.Command("document",new(){["id"]=id},lifetime.Token);
        if(!IsDisposed&&version==selectionVersion&&selectedId==id)ApplyDetails(result);
    }
    void ApplyDetails(JsonObject result)
    {
        selectedDocument=result["document"]!.AsObject();selectedPages=result["pages"]!.AsArray();var d=selectedDocument;
        previewTitle.Text=S(d,"title");previewTitle.AutoEllipsis=true;
        documentFacts.Text=$"{PipelineStatus(S(d,"status"))} · {N(d,"page_count")} 页{(N(d,"locked")==1?" · 分类已锁定":"")}\n扫描：{LocalTime(S(d,"scanned"))}\n分类：{(S(d,"category")==""?"等待秘书归档":S(d,"category"))}";
        UpdatePageSummary();contextLabel.Text=attachDocument.Checked?"将附带："+S(d,"title"):"当前问题不附带文档";
    }
    void UpdatePageSummary()
    {
        if(selectedDocument==null)return;
        string body=S(selectedDocument,"summary");if(body=="")body="内容分析尚未完成。原件已保存，你可以继续扫描；秘书将在后台处理。";
        string text="## 内容概括\n\n"+body;
        string tags=S(selectedDocument,"tags");
        if(tags!="")text+="\n\n## 关键词\n\n"+string.Join(" · ",tags.Split([',','，'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries));
        if(S(selectedDocument,"parent_id")!="")text+="\n\n## 来源追溯\n\n- 原始页码："+S(selectedDocument,"source_pages")+"\n- 原始扫描编号：`"+S(selectedDocument,"parent_id")+"`";
        if(S(selectedDocument,"error")!="")text+="\n\n## 处理提示\n\n"+S(selectedDocument,"error");
        var page=selectedPages.FirstOrDefault(p=>N(p,"number")==previewPage+1);
        if(page!=null)text+=$"\n\n---\n\n## 第 {previewPage+1} 页 · 识别原文\n\n"+S(page,"text");
        summary.SetMarkdown(text);

    }
    async Task RenderPage(int page)
    {
        if(previewPath=="")return;page=Math.Clamp(page,0,previewPages-1);int version=++renderVersion;string path=previewPath;
        previewHint.Text="正在渲染页面…";previewHint.Visible=true;previewHint.BringToFront();
        await renderGate.WaitAsync(lifetime.Token);
        try{
            if(version!=renderVersion)return;
            var rendered=await Task.Run(()=>{if(Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase)){using var pdf=new PdfPreviewDocument(path,1400,1900);return (image:pdf.RenderPage(Math.Min(page,pdf.PageCount-1)),count:pdf.PageCount);}return (image:CreatePreview(path),count:1);},lifetime.Token);
            if(IsDisposed||version!=renderVersion){rendered.image.Dispose();return;}
            previewPages=rendered.count;previewPage=Math.Min(page,previewPages-1);preview.Image?.Dispose();preview.Image=rendered.image;previewHint.Visible=false;
            jumping=true;pageJump.Maximum=previewPages;pageJump.Value=previewPage+1;jumping=false;
            pageNumber.Text=$"{previewPage+1} / {previewPages}";previousPage.Enabled=previewPage>0;nextPage.Enabled=previewPage+1<previewPages;UpdatePageSummary();
        }catch(Exception ex){if(!IsDisposed&&version==renderVersion){preview.Image?.Dispose();preview.Image=null;previewHint.Text="无法预览页面\n"+ex.Message;previewHint.Visible=true;}}
        finally{renderGate.Release();}
    }
    Task ChangePdfPage(int amount)=>RenderPage(previewPage+amount);
    static Bitmap CreatePreview(string path){using var source=Image.FromFile(path);double scale=Math.Min(1,Math.Min(1400.0/source.Width,1900.0/source.Height));return new Bitmap(source,Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)));}
    void ShowPreview(Image image,string hint){renderVersion++;preview.Image?.Dispose();preview.Image=image;previewHint.Visible=false;previewTitle.Text=hint;}
    void OpenSelected(){if(selectedDocument!=null)Open(S(selectedDocument,"original"));}
    ContextMenuStrip FileMenu()
    {
        var menu=new ContextMenuStrip();
        void Add(string title,Func<Task> action)=>menu.Items.Add(title,null,async(_,_)=>await RunUi(action));
        Add("打开原件",()=>{OpenSelected();return Task.CompletedTask;});
        Add("在文件夹中显示",()=>{if(selectedDocument!=null)Open(Path.GetDirectoryName(S(selectedDocument,"path"))!);return Task.CompletedTask;});
        Add("修改标题和分类…",EditSelected);Add("询问秘书",()=>{AskAboutSelected();return Task.CompletedTask;});Add("允许秘书重新调整分类",()=>DocumentAction("unlock"));
        menu.Items.Add(new ToolStripSeparator());Add("移入回收站",DeleteSelected);return menu;
    }
    async Task DocumentAction(string action)
    {
        if(selectedId==""){status.Text="请先选择文档。";return;}
        await SecretaryIntegration.Command(action,new(){["id"]=selectedId});status.Text=action=="unlock"?"已允许秘书调整此文档":action=="format_summary"?"Markdown 整理已排队 · 完成后概括会自动更新":"任务已加入队列，可在处理记录中查看";await RefreshFiles(true);
    }
    async Task EditSelected()
    {
        if(selectedDocument==null)return;string id=selectedId;
        using var dialog=new Form{Text="修改归档",Size=new Size(520,320),StartPosition=FormStartPosition.CenterParent,Font=Font,BackColor=Color.White,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        var title=new TextBox{Text=S(selectedDocument,"title"),Dock=DockStyle.Top};var category=new TextBox{Text=S(selectedDocument,"category"),Dock=DockStyle.Top,PlaceholderText="例如：学习/物理"};
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new Padding(22),WrapContents=false};
        foreach(var control in new Control[]{new Label{Text="文件标题",AutoSize=true},title,new Label{Text="分类目录（用 / 分隔层级）",AutoSize=true},category,new Label{Text="保存后锁定分类，秘书将尊重你的选择。",AutoSize=true,ForeColor=Muted}}){control.Width=450;control.Margin=new Padding(0,0,0,10);panel.Controls.Add(control);}
        var save=PrimaryButton("保存归档",110);save.DialogResult=DialogResult.OK;panel.Controls.Add(save);dialog.Controls.Add(panel);dialog.AcceptButton=save;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(string.IsNullOrWhiteSpace(title.Text)||string.IsNullOrWhiteSpace(category.Text))throw new ArgumentException("标题和分类不能为空。");
        await SecretaryIntegration.Command("move",new(){["id"]=id,["title"]=title.Text,["category"]=category.Text});status.Text="归档已更新 · 已锁定手动分类";await RefreshFiles(true);await RefreshDetails(id,selectionVersion);
    }
    async Task DeleteSelected()
    {
        if(selectedDocument==null)return;string deleteId=selectedId;
        if(MessageBox.Show(this,"将“"+S(selectedDocument,"title")+"”移入回收站？\n\n桌面和网页会同步隐藏，可在处理记录中恢复。","移入回收站",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        await SecretaryIntegration.Trash(deleteId);selectedId="";selectedDocument=null;selectionVersion++;renderVersion++;previewPath="";
        preview.Image?.Dispose();preview.Image=null;previewHint.Text="文档已移入回收站，可在处理记录中恢复。";previewHint.Visible=true;previewHint.BringToFront();summary.Clear();documentFacts.Text="";
        if(searchQuery!="")await SearchNow();else await RefreshFiles(true);status.Text="已移入回收站";
    }
    async Task ImportFiles()
    {
        using var picker=new OpenFileDialog{Filter="文档|*.pdf;*.png;*.jpg;*.jpeg",Multiselect=true,Title="导入到同一文档库"};if(picker.ShowDialog(this)!=DialogResult.OK)return;
        int done=0;foreach(string path in picker.FileNames){status.Text=$"正在导入 {done+1}/{picker.FileNames.Length}…";await SecretaryIntegration.Command("import",new(){["path"]=path});done++;}
        searchBox.Clear();searchQuery="";libraryOffset=0;stateFilter.Enabled=true;await RefreshFiles(true);status.Text=$"已导入 {done} 份文档 · 将自动分析与整理";
    }
}
