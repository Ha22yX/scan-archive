using System.Text.Json.Nodes;

namespace ScanArchive;

public sealed partial class MainWindow
{
    // Synthetic fixtures only. This mode never reads the library, starts the core or calls OpenAI.
    void LoadDemo()
    {
        connection.Text="● 演示文档库\n本机与远程同步";status.Text="准备就绪 · 放入纸张即可开始扫描";
        listTitle.Text="文档库 · 3 份";listHint.Text="1–3 / 3 · 所有文档已归档";
        var row=new JsonObject{["id"]="demo",["title"]="设备保修凭证",["status"]="ready",["category"]="生活 / 保修",["scanned"]="2026-09-29T15:24:00-04:00",["page_count"]=2,["summary"]="### 文件概览\n\n这份文件记录了设备的**购买信息**与保修条款。\n\n### 重要信息\n\n| 项目 | 内容 |\n| --- | --- |\n| 购买日期 | 2026-09-29 |\n| 保修期限 | 24 个月 |\n\n### 申请服务\n\n1. 准备购买凭证。\n2. 提供设备序列号。\n\n> 请保留原件，重要信息以原文为准。\n\n### 可以查询\n\n- 保修到期时间\n- 售后申请所需材料",["tags"]="设备、保修、购买凭证、warranty"};
        PopulateFiles(new JsonArray(row,new JsonObject{["id"]="demo2",["title"]="物理课堂笔记",["status"]="ready",["category"]="学习 / 物理"},new JsonObject{["id"]="demo3",["title"]="家庭资料清单",["status"]="ready",["category"]="生活 / 家庭"}),false);
        selectedDocument=row;selectedId="demo";selectedPages=new();ApplyDetails(new JsonObject{["document"]=row.DeepClone(),["pages"]=new JsonArray()});
        var bitmap=new Bitmap(700,960);using(var g=Graphics.FromImage(bitmap)){
            g.Clear(Color.White);using var titleFont=new Font("Microsoft YaHei UI",25,FontStyle.Bold);using var bodyFont=new Font("Microsoft YaHei UI",16);using var ink=new SolidBrush(Ink);using var muted=new SolidBrush(Muted);using var accent=new SolidBrush(Accent);
            g.FillRectangle(accent,56,62,58,8);g.DrawString("设备保修凭证",titleFont,ink,56,103);g.DrawString("WARRANTY DOCUMENT  /  SAMPLE",bodyFont,muted,56,162);
            string[] lines=["购买日期     2026 年 09 月 29 日","保修期限     自购买之日起 24 个月","凭证类型     电子归档副本","","服务说明","请妥善保存购买凭证。","申请保修时，请准备设备序列号及凭证。","","本页为界面演示生成的虚构资料。"];
            int y=248;foreach(string line in lines){g.DrawString(line,bodyFont,ink,56,y);y+=52;}
            g.DrawLine(Pens.LightGray,56,830,644,830);g.DrawString("SCAN ARCHIVE   ·   原件始终保留",bodyFont,muted,56,862);
        }
        ShowPreview(bitmap,"设备保修凭证");pageNumber.Text="1 / 2";pageJump.Maximum=2;
    }
    internal static void UiSmoke(string folder)
    {
        Directory.CreateDirectory(folder);
        using var form=new MainWindow(true);form.Show();Application.DoEvents();
        foreach(var size in new[]{new Size(1500,920),new Size(1100,700)}){
            form.Size=size;form.PerformLayout();Application.DoEvents();
            if(form.files.Width<120||form.preview.Width<200||form.messageBox.Width<150||form.scan.Width<140)throw new Exception("Workspace controls are clipped.");
            using var image=new Bitmap(form.Width,form.Height);form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(folder,$"desktop-{size.Width}.png"));
        }
        form.Size=new Size(1500,920);form.inspector.SelectedIndex=1;Application.DoEvents();
        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(folder,"desktop-secretary.png"));}
        form.ShowSettings().GetAwaiter().GetResult();Application.DoEvents();
        using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new Rectangle(Point.Empty,form.Size));image.Save(Path.Combine(folder,"desktop-settings.png"));}
        File.WriteAllText(Path.Combine(folder,"ui-smoke.txt"),"PASS: native workspace at 1500 and 1100 px; settings and secretary rendered; synthetic data only.");form.Close();
    }
}
