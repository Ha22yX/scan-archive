using System.Runtime.InteropServices;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ScanArchive;

// Markdown is parsed as data and rendered into the native RichEdit control.
// No HTML engine, scripts, remote resources or automatic link navigation.
public sealed class MarkdownView : RichTextBox
{
    string markdown="";
    int renderedWidth;
    public string MarkdownSource=>markdown;
    public bool IsAtEnd
    {
        get{
            if(!IsHandleCreated||TextLength==0)return true;
            var info=new ScrollInfo{Size=Marshal.SizeOf<ScrollInfo>(),Mask=7};
            return !GetScrollInfo(Handle,1,ref info)||info.Position+(int)info.Page>=info.Maximum-32;
        }
    }
    public void ScrollToEnd(){Select(TextLength,0);ScrollToCaret();}
    public MarkdownView()
    {
        ReadOnly=true;BorderStyle=BorderStyle.None;BackColor=Color.White;DetectUrls=false;
        Dock=DockStyle.Fill;ScrollBars=RichTextBoxScrollBars.Vertical;
        var menu=new ContextMenuStrip();
        menu.Items.Add("复制选中文字",null,(_,_)=>Copy());
        menu.Items.Add("复制 Markdown",null,(_,_)=>{if(markdown.Length>0)Clipboard.SetText(markdown);});
        menu.Items.Add("全选",null,(_,_)=>SelectAll());ContextMenuStrip=menu;
        Resize+=(_,_)=>{if(markdown.Length>0&&Math.Abs(ClientSize.Width-renderedWidth)>8)Render(true);};
    }
    public void SetMarkdown(string value,bool resetScroll=false)
    {
        if(value==markdown)return;markdown=value;Render(!resetScroll);
    }
    public new void Clear(){markdown="";base.Clear();}
    void Render(bool preserve)
    {
        int start=SelectionStart,length=SelectionLength;var scroll=new NativePoint();
        if(preserve&&IsHandleCreated)SendMessage(Handle,0x04DD,IntPtr.Zero,ref scroll);
        renderedWidth=ClientSize.Width;
        Rtf=MarkdownRtf.Render(markdown,Math.Max(180,(ClientSize.Width-16)*96/DeviceDpi));
        if(preserve){Select(Math.Min(start,TextLength),Math.Min(length,Math.Max(0,TextLength-start)));if(IsHandleCreated)SendMessage(Handle,0x04DE,IntPtr.Zero,ref scroll);}
        else {Select(0,0);ScrollToCaret();}
    }
    [StructLayout(LayoutKind.Sequential)]struct NativePoint{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]struct ScrollInfo{public int Size,Mask,Minimum,Maximum;public uint Page;public int Position,TrackPosition;}
    [DllImport("user32.dll",CharSet=CharSet.Auto)]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,ref NativePoint point);
    [DllImport("user32.dll")]static extern bool GetScrollInfo(IntPtr window,int bar,ref ScrollInfo info);
}

internal static class MarkdownRtf
{
    static readonly MarkdownPipeline Pipeline=new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();
    public static string Render(string markdown,int width=400)
    {
        var output=new StringBuilder(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 Microsoft YaHei UI;}{\f1 Consolas;}}{\colortbl;\red27\green54\blue51;\red32\green105\blue86;\red237\green244\blue240;\red111\green130\blue126;}\viewkind4\f0\fs21\cf1 ");
        foreach(var block in Markdown.Parse(markdown,Pipeline))Block(output,block,0,width);
        return output.Append('}').ToString();
    }
    static void Text(StringBuilder o,string text)
    {
        foreach(char c in text){switch(c){case '\\':o.Append(@"\\");break;case '{':o.Append(@"\{");break;case '}':o.Append(@"\}");break;case '\r':break;case '\n':o.Append(@"\line ");break;case '\t':o.Append(@"\tab ");break;default:if(c>127)o.Append(@"\u").Append((short)c).Append('?');else o.Append(c);break;}}
    }
    static void Inline(StringBuilder o,ContainerInline? items)
    {
        if(items==null)return;
        foreach(var item in items){switch(item){
            case LiteralInline literal:Text(o,literal.Content.ToString());break;
            case CodeInline code:o.Append(@"{\f1\highlight3 ");Text(o,code.Content);o.Append('}');break;
            case EmphasisInline emphasis:
                o.Append('{').Append(emphasis.DelimiterChar=='~'?@"\strike ":emphasis.DelimiterCount>=2?@"\b ":@"\i ");Inline(o,emphasis);o.Append('}');break;
            case LinkInline link:
                if(link.IsImage){Text(o,"[图片：");Inline(o,link);Text(o,"]");}
                else{o.Append(@"{\ul\cf2 ");Inline(o,link);o.Append('}');if(!string.IsNullOrWhiteSpace(link.Url)&&!link.Url.StartsWith("scanarchive-reference:",StringComparison.Ordinal)){Text(o," (");Text(o,link.Url);Text(o,")");}}break;
            case AutolinkInline link:Text(o,link.Url);break;
            case LineBreakInline line:o.Append(line.IsHard?@"\line ":" ");break;
            case HtmlInline html:Text(o,html.Tag);break;
            case ContainerInline nested:Inline(o,nested);break;
        }}
    }
    static void Paragraph(StringBuilder o,int indent)=>o.Append(@"\pard\f0\fs21\cf1\sb60\sa120\sl300\slmult1\li").Append(indent).Append(@"\fi0 ");
    static void Block(StringBuilder o,Block block,int indent,int width)
    {
        switch(block){
            case Table table:
                int count=table.OfType<TableRow>().Select(x=>x.Count).DefaultIfEmpty(1).Max();
                foreach(var row in table.OfType<TableRow>()){
                    o.Append(@"\trowd\trgaph90\trleft0 ");
                    for(int n=1;n<=count;n++){o.Append(@"\clbrdrb\brdrs\brdrw8\brdrcf3");if(row.IsHeader)o.Append(@"\clcbpat3");o.Append(@"\cellx").Append(width*15*n/count).Append(' ');}
                    foreach(var cell in row.OfType<TableCell>()){
                        o.Append(@"\pard\intbl\f0\fs20\cf1\sa80 ");if(row.IsHeader)o.Append(@"\b ");
                        foreach(var child in cell){if(child is LeafBlock leaf)Inline(o,leaf.Inline);}
                        o.Append(@"\b0\cell ");
                    }o.Append(@"\row ");
                }Paragraph(o,indent);o.Append(@"\par ");break;
            case HeadingBlock heading:
                Paragraph(o,indent);o.Append(@"\sb220\sa120\b\cf2\fs").Append(heading.Level switch{1=>32,2=>27,_=>23}).Append(' ');Inline(o,heading.Inline);o.Append(@"\b0\par ");break;
            case ListBlock list:
                int number=int.TryParse(list.OrderedStart,out int first)?first:1;
                foreach(var item in list.OfType<ListItemBlock>()){
                    bool initial=true;foreach(var child in item){
                        if(initial&&child is ParagraphBlock paragraph){Paragraph(o,indent+260);o.Append(@"\fi-220 ");Text(o,list.IsOrdered?$"{number++}. ":"• ");Inline(o,paragraph.Inline);o.Append(@"\par ");}
                        else Block(o,child,indent+260,width-18);initial=false;
                    }
                }break;
            case QuoteBlock quote:
                o.Append('{');foreach(var child in quote){Paragraph(o,indent+240);o.Append(@"\cf4\i ");if(child is ParagraphBlock p){Inline(o,p.Inline);o.Append(@"\par ");}else Block(o,child,indent+240,width-16);}o.Append('}');break;
            case CodeBlock code:
                Paragraph(o,indent+120);o.Append(@"\f1\fs20\highlight3 ");Text(o,code.Lines.ToString());o.Append(@"\highlight0\par ");break;
            case ThematicBreakBlock:Paragraph(o,indent);Text(o,"────────────────");o.Append(@"\par ");break;
            case LeafBlock leaf:
                Paragraph(o,indent);if(leaf.Inline!=null)Inline(o,leaf.Inline);else Text(o,leaf.Lines.ToString());o.Append(@"\par ");break;
            case ContainerBlock container:foreach(var child in container)Block(o,child,indent,width);break;
        }
    }
}
