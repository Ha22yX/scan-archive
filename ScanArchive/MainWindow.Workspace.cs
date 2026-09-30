using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    readonly TextBox searchBox=new(){Dock=DockStyle.Fill,PlaceholderText="搜索内容、编号、姓名，或描述你要找的文件…",BorderStyle=BorderStyle.None};
    readonly ComboBox categoryFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=160},stateFilter=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=110};
    readonly ListView files=new DocumentListView(){Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BorderStyle=BorderStyle.None,ShowItemToolTips=true};
    readonly Label listTitle=Caption("文档库"),listHint=new(){Dock=DockStyle.Bottom,Height=48,ForeColor=Muted,Text="扫描或导入文件，即可建立文档库。",AutoEllipsis=true};
    readonly Label previewTitle=Caption("原件预览"),previewHint=new(){Text="选择一份文档，或开始扫描\n\n原始页面会一直为你保留",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Muted};
    readonly PictureBox preview=new(){Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Surface};
    readonly FlowLayoutPanel pdfNavigation=new(){Dock=DockStyle.Bottom,Height=43,WrapContents=false,Padding=new Padding(0,5,0,0)};
    readonly Button previousPage=new(){Text="‹",Width=36,Height=30},nextPage=new(){Text="›",Width=36,Height=30};
    readonly Label pageNumber=new(){Text="—",Width=64,Height=30,TextAlign=ContentAlignment.MiddleCenter};
    readonly NumericUpDown pageJump=new(){Minimum=1,Maximum=1,Width=55,Height=30};
    readonly MarkdownView summary=new();
    readonly Label documentFacts=new(){Dock=DockStyle.Top,Height=90,ForeColor=Muted,AutoEllipsis=true};
    readonly TabControl inspector=new(){Dock=DockStyle.Fill,Appearance=TabAppearance.FlatButtons,SizeMode=TabSizeMode.Fixed,ItemSize=new Size(112,36),Padding=new Point(12,8)};
    readonly ComboBox detailMode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill};
    readonly ComboBox zoomMode=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=105};
    readonly Panel previewCanvas=new(){Dock=DockStyle.Fill,BackColor=Color.FromArgb(234,239,234),AutoScroll=true,Padding=new Padding(14)};
    readonly SplitContainer librarySplit=new(){Dock=DockStyle.Fill,SplitterWidth=8,Panel1MinSize=0,Panel2MinSize=0,BackColor=Surface};
    readonly SplitContainer readingSplit=new(){Dock=DockStyle.Fill,SplitterWidth=8,Panel1MinSize=0,Panel2MinSize=0,BackColor=Surface};
    readonly Label libraryEmpty=new(){Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Muted,Visible=false};
    Button? focusButton,resetSearch,detailMore;
    bool focusReading;
    readonly List<Control> documentActions=new();
    readonly Button listPrevious=new(){Text="上一批",Width=72,Height=30},listNext=new(){Text="下一批",Width=72,Height=30};
    string searchQuery="",librarySignature="",selectedId="",previewPath="";
    int libraryOffset,libraryTotal,listVersion,selectionVersion,renderVersion,previewPage,previewPages=1;
    bool filterLoading,jumping,pageRendering;
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
        var toolbar=new TableLayoutPanel{Dock=DockStyle.Top,Height=62,ColumnCount=4,RowCount=1,Margin=Padding.Empty};
        toolbar.ColumnStyles.Add(new(SizeType.Percent,100));foreach(int width in new[]{84,82,104})toolbar.ColumnStyles.Add(new(SizeType.Absolute,width));
        var searchFrame=new Panel{Dock=DockStyle.Fill,BackColor=Color.White,Padding=new Padding(14,11,12,9),Margin=new Padding(0,0,8,16)};
        searchFrame.Paint+=(_,e)=>ControlPaint.DrawBorder(e.Graphics,searchFrame.ClientRectangle,Line,ButtonBorderStyle.Solid);
        searchFrame.Controls.Add(searchBox);searchBox.Dock=DockStyle.Fill;searchBox.AccessibleName="搜索文档";tips.SetToolTip(searchBox,"搜索内容、姓名或编号 · Ctrl + F");
        toolbar.Controls.Add(searchFrame,0,0);var find=ActionButton("搜索",SearchNow,this,76);find.Height=44;toolbar.Controls.Add(find,1,0);
        resetSearch=ActionButton("重置",ResetLibraryFilters,this,74);resetSearch.Height=44;toolbar.Controls.Add(resetSearch,2,0);
        var import=ActionButton("导入文件",ImportFiles,this,98);import.Height=44;toolbar.Controls.Add(import,3,0);
        searchBox.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;await RunUi(SearchNow);}};
        var library=new Panel{Dock=DockStyle.Fill};
        var filters=new TableLayoutPanel{Dock=DockStyle.Top,Height=50,ColumnCount=2,RowCount=1};filters.ColumnStyles.Add(new(SizeType.Percent,60));filters.ColumnStyles.Add(new(SizeType.Percent,40));
        categoryFilter.Items.Add("全部分类");categoryFilter.SelectedIndex=0;categoryFilter.Dock=DockStyle.Fill;
        stateFilter.Items.AddRange(["全部状态","等待分析","逐页分析","建立索引","等待整理","已归档","需要重试"]);stateFilter.SelectedIndex=0;stateFilter.Dock=DockStyle.Fill;
        categoryFilter.AccessibleName="按分类筛选";stateFilter.AccessibleName="按处理状态筛选";
        filters.Controls.Add(categoryFilter,0,0);filters.Controls.Add(stateFilter,1,0);
        async void Changed(object? sender,EventArgs e){if(filterLoading||demo)return;libraryOffset=0;await RunUi(async()=>{if(searchQuery!="")await SearchNow();else await RefreshFiles(true);});}
        categoryFilter.SelectedIndexChanged+=Changed;stateFilter.SelectedIndexChanged+=Changed;
        files.Columns.Add("文档",280);files.AccessibleName="文档列表";
        files.Resize+=(_,_)=>{if(files.Columns.Count>0)files.Columns[0].Width=Math.Max(90,files.ClientSize.Width-4);};
        files.SelectedIndexChanged+=async(_,_)=>{if(!refreshingFiles&&files.SelectedItems.Count>0&&!demo)await RunUi(()=>SelectDocument((JsonObject)files.SelectedItems[0].Tag!));};
        files.MouseDoubleClick+=async(_,e)=>{if(e.Button==MouseButtons.Left&&files.GetItemAt(e.X,e.Y)?.Tag is JsonObject row)await RunUi(()=>OpenListDocument((JsonObject)row.DeepClone()));};
        files.KeyDown+=async(_,e)=>{if(e.KeyCode==Keys.Enter&&files.SelectedItems.Count>0){e.SuppressKeyPress=true;var row=(JsonObject)((JsonObject)files.SelectedItems[0].Tag!).DeepClone();await RunUi(()=>OpenListDocument(row));}};
        files.MouseDown+=(_,e)=>{if(e.Button!=MouseButtons.Right)return;if(files.GetItemAt(e.X,e.Y) is { } item)item.Selected=true;else foreach(ListViewItem selected in files.SelectedItems.Cast<ListViewItem>().ToArray())selected.Selected=false;};
        files.ContextMenuStrip=FileMenu();
        var listBody=new Panel{Dock=DockStyle.Fill};listBody.Controls.Add(files);listBody.Controls.Add(libraryEmpty);libraryEmpty.BringToFront();
        var paging=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=38};StyleSmallButton(listPrevious);StyleSmallButton(listNext);paging.Controls.AddRange([listPrevious,listNext]);
        listPrevious.Click+=async(_,_)=>await RunUi(async()=>{libraryOffset=Math.Max(0,libraryOffset-60);await RefreshFiles(true);});
        listNext.Click+=async(_,_)=>await RunUi(async()=>{libraryOffset+=60;await RefreshFiles(true);});
        library.Controls.Add(listBody);library.Controls.Add(listHint);library.Controls.Add(paging);library.Controls.Add(filters);library.Controls.Add(listTitle);listBody.BringToFront();
        librarySplit.Panel1.Controls.Add(Card("",library));librarySplit.Panel2.Controls.Add(readingSplit);
        var viewer=new Panel{Dock=DockStyle.Fill};previewTitle.Height=68;previewTitle.TextAlign=ContentAlignment.TopLeft;previewTitle.Padding=new Padding(0,5,0,0);
        previewCanvas.Controls.Add(preview);previewCanvas.Controls.Add(previewHint);previewHint.BringToFront();
        previewCanvas.Resize+=(_,_)=>ApplyZoom();
        pdfNavigation.Height=46;StyleSmallButton(previousPage);StyleSmallButton(nextPage);
        pdfNavigation.Controls.AddRange([previousPage,pageNumber,nextPage,pageJump]);
        previousPage.AccessibleName="上一页";nextPage.AccessibleName="下一页";pageJump.AccessibleName="跳到页码";
        previousPage.Click+=async(_,_)=>await RunUi(()=>ChangePdfPage(-1));nextPage.Click+=async(_,_)=>await RunUi(()=>ChangePdfPage(1));
        pageJump.ValueChanged+=async(_,_)=>{if(!jumping)await RunUi(()=>RenderPage((int)pageJump.Value-1));};
        var viewerTools=new FlowLayoutPanel{Dock=DockStyle.Top,Height=44,WrapContents=false};
        zoomMode.Items.AddRange(["适应页面","75%","100%","150%","200%"]);zoomMode.SelectedIndex=0;zoomMode.AccessibleName="预览缩放";zoomMode.SelectedIndexChanged+=(_,_)=>ApplyZoom();
        var open=ActionButton("打开原件",()=>{OpenSelected();return Task.CompletedTask;},this,90);documentActions.Add(open);
        focusButton=ActionButton("专注阅读",()=>{ToggleFocusReading();return Task.CompletedTask;},this,90);viewerTools.Controls.AddRange([zoomMode,open,focusButton]);
        viewer.Controls.Add(previewCanvas);viewer.Controls.Add(pdfNavigation);viewer.Controls.Add(viewerTools);viewer.Controls.Add(previewTitle);previewCanvas.BringToFront();readingSplit.Panel1.Controls.Add(Card("",viewer));
        var detailsTab=new TabPage("文档详情"){BackColor=Color.White,Padding=new Padding(10,14,10,8)};
        var tools=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,WrapContents=false,Padding=new Padding(0,9,0,0)};
        var edit=ActionButton("修改归档",EditSelected,this,92);var ask=ActionButton("询问秘书",()=>{AskAboutSelected();return Task.CompletedTask;},this,92);
        detailMore=ActionButton("更多 ⋯",()=>{ShowDocumentMenu();return Task.CompletedTask;},this,78);tools.Controls.AddRange([edit,ask,detailMore]);documentActions.AddRange([edit,ask,detailMore]);
        var modeRow=new Panel{Dock=DockStyle.Top,Height=44,Padding=new Padding(0,3,0,10)};detailMode.Items.AddRange(["内容概览","本页识别原文","关键词与来源"]);detailMode.SelectedIndex=0;detailMode.SelectedIndexChanged+=(_,_)=>UpdatePageSummary();modeRow.Controls.Add(detailMode);
        documentFacts.Height=88;
        detailsTab.Controls.Add(summary);detailsTab.Controls.Add(tools);detailsTab.Controls.Add(modeRow);detailsTab.Controls.Add(documentFacts);summary.BringToFront();
        inspector.TabPages.Add(detailsTab);inspector.TabPages.Add(BuildSecretary());readingSplit.Panel2.Controls.Add(Card("",inspector));
        page.Controls.Add(librarySplit);page.Controls.Add(toolbar);librarySplit.BringToFront();homePage=page;
        // Apply minimum widths only after the containers have received real layout bounds.
        page.SizeChanged+=(_,_)=>LayoutPanes();page.HandleCreated+=(_,_)=>BeginInvoke((Action)LayoutPanes);
        summary.SetMarkdown("## 每份文件，都有条理\n\n选中文档，即可查看内容概览、原始页面和扫描时间。\n\n切换到 **文档秘书**，直接描述你想找的资料。");
        UpdateDocumentActions();
    }
    void LayoutPanes()
    {
        if(focusReading||librarySplit.Width<600||readingSplit.Width<200)return;
        int scale=DeviceDpi;int left=Math.Clamp(librarySplit.Width*26/100,240*scale/96,340*scale/96);
        if(librarySplit.Panel1MinSize==0){librarySplit.SplitterDistance=left;librarySplit.Panel1MinSize=220*scale/96;librarySplit.Panel2MinSize=540*scale/96;}
        int available=readingSplit.Width-readingSplit.SplitterWidth;
        if(available>520*scale/96){readingSplit.Panel1MinSize=230*scale/96;readingSplit.Panel2MinSize=290*scale/96;readingSplit.SplitterDistance=Math.Clamp(available*54/100,readingSplit.Panel1MinSize,available-readingSplit.Panel2MinSize);}
    }
    static void StyleSmallButton(Button button){button.FlatStyle=FlatStyle.Flat;button.FlatAppearance.BorderColor=Line;button.BackColor=Color.White;button.Cursor=Cursors.Hand;}
    async Task ResetLibraryFilters(){searchBox.Clear();searchQuery="";libraryOffset=0;filterLoading=true;categoryFilter.SelectedIndex=0;stateFilter.SelectedIndex=0;filterLoading=false;stateFilter.Enabled=true;await RefreshFiles(true);}
    void ToggleFocusReading(){focusReading=!focusReading;librarySplit.Panel1Collapsed=focusReading;readingSplit.Panel2Collapsed=focusReading;focusButton!.Text=focusReading?"退出专注":"专注阅读";if(!focusReading)LayoutPanes();}
    void ApplyZoom()
    {
        if(preview.Image==null||previewCanvas.IsDisposed)return;
        if(zoomMode.SelectedIndex<=0){preview.Dock=DockStyle.Fill;preview.SizeMode=PictureBoxSizeMode.Zoom;previewCanvas.AutoScrollMinSize=Size.Empty;}
        else{double factor=zoomMode.SelectedIndex switch{1=>.75,2=>1,3=>1.5,_=>2};preview.Dock=DockStyle.None;preview.SizeMode=PictureBoxSizeMode.StretchImage;preview.Size=new Size((int)(preview.Image.Width*factor),(int)(preview.Image.Height*factor));preview.Location=new Point(previewCanvas.Padding.Left,previewCanvas.Padding.Top);previewCanvas.AutoScrollMinSize=new Size(preview.Width+28,preview.Height+28);}
    }
    void UpdateDocumentActions()
    {
        bool ready=SelectedDocumentReady;
        foreach(var control in documentActions)control.Enabled=ready;
        detailMode.Enabled=ready;
        bool pagesReady=ready&&previewPath!=""&&!pageRendering;
        pageJump.Enabled=pagesReady;previousPage.Enabled=pagesReady&&previewPage>0;nextPage.Enabled=pagesReady&&previewPage+1<previewPages;
    }
    void ShowDocumentMenu()
    {
        var menu=FileMenu(true);menu.Closed+=(_,_)=>{if(!IsDisposed&&IsHandleCreated)BeginInvoke((Action)menu.Dispose);else menu.Dispose();};menu.Show(detailMore!,new Point(0,detailMore!.Height));
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
        string previous=selectedId;int previousPage=previewPage+1;refreshingFiles=true;files.BeginUpdate();
        try{
            files.Items.Clear();
            foreach(var row in rows){if(row==null)continue;var d=row.AsObject();string id=DocumentRowId(d);
                var item=new ListViewItem(S(d,"title")){Tag=d,ToolTipText=search?$"{S(d,"title")}\n{S(d,"snippet")}":$"{S(d,"title")}\n{S(d,"category")}\n扫描于 {LocalTime(S(d,"scanned"))}\n{S(d,"error")}"};
                item.ForeColor=S(d,"status")=="error"?Color.FromArgb(166,65,51):Ink;files.Items.Add(item);if(id==previous&&(!search||N(d,"page")==previousPage))item.Selected=true;
            }
        }finally{files.EndUpdate();refreshingFiles=false;}
        libraryEmpty.Visible=rows.Count==0;libraryEmpty.Text=search?"没有找到匹配内容\n\n换个关键词，或让秘书帮你找。":"还没有文档\n\n扫描或导入一份文件，\n秘书会自动分析与归档。";
        if(rows.Count==0)listHint.Text=search?"没有找到匹配内容，试试不同关键词或询问秘书。":"这里还没有文档。开始扫描，或导入已有文件。";
    }
    async Task SearchNow()
    {
        string query=searchBox.Text.Trim();searchQuery=query;libraryOffset=0;
        if(query==""){await RefreshFiles(true);return;}
        int version=++listVersion;listTitle.Text="正在搜索…";listHint.Text="正在查找内容与相关页面…";
        listPrevious.Enabled=listNext.Enabled=false;stateFilter.Enabled=false;
        JsonObject result;
        try{result=await SecretaryIntegration.Command("search",new(){["q"]=query,["category"]=Category},lifetime.Token);}
        catch{if(version==listVersion){listTitle.Text="搜索未完成";listHint.Text="请重试，或点击重置返回文档库。";}throw;}
        if(IsDisposed||version!=listVersion)return;
        var rows=result["results"]!.AsArray();PopulateFiles(rows,true);listTitle.Text=$"匹配页面 · {rows.Count}";
        listHint.Text=(S(result,"mode")=="hybrid"?"语义 + 关键词检索":"关键词检索")+" · 点击结果定位原文页";
        if(rows.Count==0)listHint.Text="未找到匹配页面。可换个描述，或请秘书继续查找。";
        listPrevious.Enabled=listNext.Enabled=false;
        stateFilter.Enabled=false;
    }
    async Task SelectDocument(JsonObject row)
    {
        string id=DocumentRowId(row);if(id=="")return;
        bool changed=id!=selectedId;
        int targetPage=N(row,"page")>0?N(row,"page")-1:changed?0:previewPage;
        bool needsPreview=changed||selectedDocument==null||previewPath==""||preview.Image==null;
        selectedId=id;int version=++selectionVersion;
        if(needsPreview){selectedDocument=null;selectedPages=new();summary.Clear();documentFacts.Text="正在读取文档…";ResetDocumentPreview("正在打开文档…");previewTitle.Text=S(row,"title")==""?"正在读取文档…":S(row,"title");}
        try{
            var result=await SecretaryIntegration.Command("document",new(){["id"]=id},lifetime.Token);
            if(IsDisposed||version!=selectionVersion||id!=selectedId)return;
            ApplyDetails(result);previewTitle.Text=S(selectedDocument,"title");
            if(needsPreview||targetPage!=previewPage){previewPath=S(selectedDocument,"original");previewPages=Math.Max(1,N(selectedDocument,"page_count"));await RenderPage(targetPage);}
        }catch{
            if(!IsDisposed&&version==selectionVersion&&needsPreview){documentFacts.Text="文档读取未完成";previewHint.Text="无法加载文档，请重新选择后重试。";previewHint.Visible=true;UpdateDocumentActions();}
            throw;
        }
    }
    internal static string DocumentRowId(JsonNode? row)=>S(row,"id") is { Length:>0 } id?id:S(row,"doc_id");
    bool SelectedDocumentReady=>selectedId!=""&&selectedDocument!=null&&DocumentRowId(selectedDocument)==selectedId;
    void ResetDocumentPreview(string hint)
    {
        renderVersion++;pageRendering=false;previewPath="";previewPage=0;previewPages=1;
        preview.Image?.Dispose();preview.Image=null;previewHint.Text=hint;previewHint.Visible=true;previewHint.BringToFront();pageNumber.Text="—";
        jumping=true;try{pageJump.Maximum=1;pageJump.Value=1;}finally{jumping=false;}
        UpdateDocumentActions();UpdateChatContext();
    }
    async Task<bool> EnsureActionDocument(JsonObject row)
    {
        if(demo)return false;
        string id=DocumentRowId(row);if(id=="")return false;
        if(id!=selectedId||!SelectedDocumentReady)await SelectDocument(row);
        else if(N(row,"page")>0&&previewPage+1!=N(row,"page"))await RenderPage(N(row,"page")-1);
        if(IsDisposed||id!=selectedId||!SelectedDocumentReady){if(!IsDisposed)status.Text="选择已改变，请在需要操作的文档上重试。";return false;}
        return true;
    }
    async Task OpenListDocument(JsonObject row){if(await EnsureActionDocument(row))OpenSelected();}
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
        tips.SetToolTip(previewTitle,S(d,"title"));tips.SetToolTip(documentFacts,documentFacts.Text);UpdatePageSummary();UpdateChatContext();UpdateDocumentActions();
    }
    void UpdatePageSummary()
    {
        if(selectedDocument==null)return;
        string body=S(selectedDocument,"summary");if(body=="")body="内容分析尚未完成。原件已保存，你可以继续扫描；秘书将在后台处理。";
        string text=body;
        if(detailMode.SelectedIndex==1){var page=selectedPages.FirstOrDefault(p=>N(p,"number")==previewPage+1);text=$"## 第 {previewPage+1} 页 · 识别原文\n\n"+(page==null?"本页尚未完成识别。你可以先查看左侧原件。":S(page,"text"));}
        if(detailMode.SelectedIndex==2){text="## 检索关键词\n\n"+S(selectedDocument,"tags")+"\n\n## 扫描记录\n\n- 扫描时间："+LocalTime(S(selectedDocument,"scanned"));
            if(S(selectedDocument,"parent_id")!="")text+="\n- 原始页码："+S(selectedDocument,"source_pages")+"\n\n可从更多菜单中查看原始扫描。";}
        if(S(selectedDocument,"error")!="")text+="\n\n## 处理提示\n\n"+S(selectedDocument,"error");
        summary.SetMarkdown(text);

    }
    async Task RenderPage(int page)
    {
        if(previewPath==""||!SelectedDocumentReady)return;
        page=Math.Clamp(page,0,previewPages-1);int version=++renderVersion;string path=previewPath,documentId=selectedId;
        pageRendering=true;UpdateDocumentActions();previewHint.Text="正在渲染页面…";previewHint.Visible=true;previewHint.BringToFront();
        bool entered=false;
        try{
            await renderGate.WaitAsync(lifetime.Token);entered=true;
            if(version!=renderVersion||documentId!=selectedId||path!=previewPath)return;
            var rendered=await Task.Run(()=>{if(Path.GetExtension(path).Equals(".pdf",StringComparison.OrdinalIgnoreCase)){using var pdf=new PdfPreviewDocument(path,1400,1900);return (image:pdf.RenderPage(Math.Min(page,pdf.PageCount-1)),count:pdf.PageCount);}return (image:CreatePreview(path),count:1);},lifetime.Token);
            if(IsDisposed||version!=renderVersion||documentId!=selectedId||path!=previewPath){rendered.image.Dispose();return;}
            previewPages=rendered.count;previewPage=Math.Min(page,previewPages-1);preview.Image?.Dispose();preview.Image=rendered.image;previewHint.Visible=false;ApplyZoom();UpdateChatContext();
            jumping=true;try{pageJump.Maximum=previewPages;pageJump.Value=previewPage+1;}finally{jumping=false;}
            pageNumber.Text=$"{previewPage+1} / {previewPages}";UpdatePageSummary();
        }catch(Exception ex){if(!IsDisposed&&version==renderVersion){preview.Image?.Dispose();preview.Image=null;previewHint.Text="无法预览页面\n"+ex.Message;previewHint.Visible=true;}}
        finally{if(entered)renderGate.Release();if(!IsDisposed&&version==renderVersion){pageRendering=false;UpdateDocumentActions();}}
    }
    Task ChangePdfPage(int amount)=>RenderPage(previewPage+amount);
    static Bitmap CreatePreview(string path){using var source=Image.FromFile(path);double scale=Math.Min(1,Math.Min(1400.0/source.Width,1900.0/source.Height));return new Bitmap(source,Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)));}
    void ShowPreview(Image image,string hint)
    {
        // A newly captured scan is not the previously selected library document.
        if(!demo){
            selectedId="";selectedDocument=null;selectedPages=new();selectionVersion++;
            summary.Clear();documentFacts.Text="扫描页面已采集，正在保存并交给文档库。";
            refreshingFiles=true;try{foreach(ListViewItem item in files.SelectedItems.Cast<ListViewItem>().ToArray())item.Selected=false;}finally{refreshingFiles=false;}
            ResetDocumentPreview("");
        }
        renderVersion++;pageRendering=false;preview.Image?.Dispose();preview.Image=image;previewHint.Visible=false;previewTitle.Text=hint;ApplyZoom();UpdateDocumentActions();
    }
    void OpenSelected(){if(SelectedDocumentReady)Open(S(selectedDocument,"original"));}
    ContextMenuStrip FileMenu(bool includeJobs=false)
    {
        var menu=new ContextMenuStrip();
        JsonObject? menuDocument=null;
        menu.Opening+=(_,e)=>{
            menuDocument=menu.SourceControl==files
                ?files.SelectedItems.Count>0?((JsonObject)files.SelectedItems[0].Tag!).DeepClone().AsObject():null
                :selectedId==""?null:new JsonObject{["id"]=selectedId,["page"]=previewPage+1};
            e.Cancel=menuDocument==null||DocumentRowId(menuDocument)=="";
        };
        void Add(string title,Func<Task> action)=>menu.Items.Add(title,null,async(_,_)=>{
            var target=menuDocument?.DeepClone().AsObject();if(target==null)return;
            await RunUi(async()=>{if(await EnsureActionDocument(target))await action();});
        });
        if(includeJobs){Add("重新整理",()=>DocumentAction("organize"));Add("整理概括排版",()=>DocumentAction("format_summary"));Add("重试内容分析",()=>DocumentAction("retry_analysis"));menu.Items.Add(new ToolStripSeparator());}
        Add("打开原件",()=>{OpenSelected();return Task.CompletedTask;});
        Add("在文件夹中显示",()=>{if(selectedDocument!=null)Open(Path.GetDirectoryName(S(selectedDocument,"path"))!);return Task.CompletedTask;});
        Add("查看原始扫描",async()=>{string parent=S(selectedDocument,"parent_id");if(parent==""){status.Text="当前文档就是原始扫描。";return;}await SelectDocument(new JsonObject{["id"]=parent});if(selectedId==parent&&SelectedDocumentReady)status.Text="正在查看后台保留的原始扫描";});
        Add("修改标题和分类…",EditSelected);Add("询问秘书",()=>{AskAboutSelected();return Task.CompletedTask;});Add("允许秘书重新调整分类",()=>DocumentAction("unlock"));
        menu.Items.Add(new ToolStripSeparator());Add("移入回收站",DeleteSelected);return menu;
    }
    async Task DocumentAction(string action)
    {
        if(!SelectedDocumentReady){status.Text="请先选择文档并等待加载完成。";return;}
        string id=selectedId;await SecretaryIntegration.Command(action,new(){["id"]=id});status.Text=action=="unlock"?"已允许秘书调整此文档":action=="format_summary"?"Markdown 整理已排队 · 完成后概括会自动更新":"任务已加入队列，可在处理记录中查看";await RefreshFiles(true);
    }
    async Task EditSelected()
    {
        if(!SelectedDocumentReady)return;string id=selectedId;
        using var dialog=new Form{Text="修改归档",Size=new Size(560,370),StartPosition=FormStartPosition.CenterParent,Font=Font,BackColor=Color.White,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false};
        var title=new TextBox{Text=S(selectedDocument,"title"),Dock=DockStyle.Top,Multiline=true,Height=72,ScrollBars=ScrollBars.Vertical};var category=new TextBox{Text=S(selectedDocument,"category"),Dock=DockStyle.Top,PlaceholderText="例如：学习/物理"};
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new Padding(22),WrapContents=false};
        foreach(var control in new Control[]{new Label{Text="文件标题",AutoSize=true},title,new Label{Text="分类目录（用 / 分隔层级）",AutoSize=true},category,new Label{Text="保存后锁定分类，秘书将尊重你的选择。",AutoSize=true,ForeColor=Muted}}){control.Width=496;control.Margin=new Padding(0,0,0,10);panel.Controls.Add(control);}
        var save=PrimaryButton("保存归档",110);save.DialogResult=DialogResult.OK;panel.Controls.Add(save);dialog.Controls.Add(panel);dialog.AcceptButton=save;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        if(string.IsNullOrWhiteSpace(title.Text)||string.IsNullOrWhiteSpace(category.Text))throw new ArgumentException("标题和分类不能为空。");
        await SecretaryIntegration.Command("move",new(){["id"]=id,["title"]=title.Text,["category"]=category.Text});status.Text="归档已更新 · 已锁定手动分类";await RefreshFiles(true);await RefreshDetails(id,selectionVersion);
    }
    async Task DeleteSelected()
    {
        if(!SelectedDocumentReady||files.Focused&&files.SelectedItems.Count==0)return;string deleteId=selectedId;
        if(MessageBox.Show(this,"将“"+S(selectedDocument,"title")+"”移入回收站？\n\n桌面和网页会同步隐藏，可在处理记录中恢复。","移入回收站",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
        await SecretaryIntegration.Trash(deleteId);
        if(!IsDisposed&&selectedId==deleteId){selectedId="";selectedDocument=null;selectedPages=new();selectionVersion++;summary.Clear();documentFacts.Text="";previewTitle.Text="原件预览";ResetDocumentPreview("文档已移入回收站，可在处理记录中恢复。");}
        if(IsDisposed)return;
        if(searchQuery!="")await SearchNow();else await RefreshFiles(true);status.Text="已移入回收站";
    }
    async Task ImportFiles()
    {
        using var picker=new OpenFileDialog{Filter="文档|*.pdf;*.png;*.jpg;*.jpeg",Multiselect=true,Title="导入到同一文档库"};if(picker.ShowDialog(this)!=DialogResult.OK)return;
        int done=0;foreach(string path in picker.FileNames){status.Text=$"正在导入 {done+1}/{picker.FileNames.Length}…";await SecretaryIntegration.Command("import",new(){["path"]=path});done++;}
        searchBox.Clear();searchQuery="";libraryOffset=0;stateFilter.Enabled=true;await RefreshFiles(true);status.Text=$"已导入 {done} 份文档 · 将自动分析与整理";
    }
}
