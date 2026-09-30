using System.Text.Json.Nodes;

namespace ScanArchive;

// Native list semantics (keyboard selection, context menu and screen-reader names)
// with room for a document title and its filing context.
sealed class DocumentListView : ListView
{
    readonly ImageList spacing=new(){ImageSize=new Size(1,90)};
    public DocumentListView()
    {
        DoubleBuffered=true;OwnerDraw=true;HeaderStyle=ColumnHeaderStyle.None;
        SmallImageList=spacing;View=View.Details;FullRowSelect=true;HideSelection=false;
        DrawColumnHeader+=(_,e)=>e.DrawDefault=true;
        DrawItem+=(_,e)=>{};
        DrawSubItem+=DrawDocument;
    }
    void DrawDocument(object? sender,DrawListViewSubItemEventArgs e)
    {
        if(e.ColumnIndex!=0||e.Item==null)return;
        int U(int value)=>value*DeviceDpi/96;
        var r=e.Bounds;bool selected=e.Item.Selected;var ink=Color.FromArgb(27,54,51);var muted=Color.FromArgb(111,130,126);
        using(var background=new SolidBrush(selected?Color.FromArgb(231,241,234):Color.White))e.Graphics.FillRectangle(background,r);
        if(selected){using var marker=new SolidBrush(Color.FromArgb(32,105,86));e.Graphics.FillRectangle(marker,r.X,r.Y+U(10),U(3),r.Height-U(20));}
        var title=new Rectangle(r.X+U(12),r.Y+U(10),Math.Max(1,r.Width-U(24)),U(46));
        TextRenderer.DrawText(e.Graphics,e.Item.Text,Font,title,ink,TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
        var doc=e.Item.Tag as JsonObject;string category=doc?["category"]?.ToString()??"";
        string state=doc?["status"]?.ToString()??"";
        string label=state switch{"ready"=>"已归档","analyzing"=>"分析中","indexing"=>"索引中","analyzed"=>"待整理","error"=>"需处理",_=>"待分析"};
        if(doc?["doc_id"]!=null)label="第 "+doc["page"]+" 页";
        string count=doc?["page_count"]?.ToString()??"";
        string footer=label+(count is "" or "0"?"":" · "+count+" 页")+(category==""?"":"  /  "+category);
        using var small=new Font(Font.FontFamily,Font.Size-1);
        TextRenderer.DrawText(e.Graphics,footer,small,new Rectangle(r.X+U(12),r.Y+U(61),Math.Max(1,r.Width-U(24)),U(24)),state=="error"?Color.FromArgb(169,66,50):muted,TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
        using var rule=new Pen(Color.FromArgb(239,243,239));e.Graphics.DrawLine(rule,r.Left+12,r.Bottom-1,r.Right-12,r.Bottom-1);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);spacing.ImageSize=new Size(1,90*DeviceDpi/96);}
    protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);spacing.ImageSize=new Size(1,90*DeviceDpi/96);}
    protected override void OnResize(EventArgs e){base.OnResize(e);if(Columns.Count>0)Columns[0].Width=Math.Max(80,ClientSize.Width-SystemInformation.VerticalScrollBarWidth);}
    protected override void Dispose(bool disposing){if(disposing)spacing.Dispose();base.Dispose(disposing);}
}
