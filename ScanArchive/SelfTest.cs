using PdfSharp.Pdf.IO;
using System.Text.Json.Nodes;

namespace ScanArchive;

static class SelfTest
{
    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ScanArchive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string report = Path.Combine(AppContext.BaseDirectory, "self-test.txt");
        try
        {
            using(var markdown=new MarkdownView{Width=400}){
                markdown.SetMarkdown("## 标题\n\n**关键内容** 与 `code`\n\n- 第一项\n- 第二项\n\n| 字段 | 值 |\n| --- | --- |\n| 保修 | 24个月 |\n\n> 保留原件\n\n```\n{\\rtf1 注入}\n```\n\n![外部图片](https://invalid.example/a.png)");
                if(!markdown.Text.Contains("标题")||!markdown.Text.Contains("24个月")||!markdown.Text.Contains(@"{\rtf1 注入}")||markdown.Text.Contains("**"))throw new Exception("Markdown rendering lost text or failed escaping");
                markdown.Select(markdown.Text.IndexOf("关键内容",StringComparison.Ordinal),4);
                if(markdown.SelectionFont?.Bold!=true)throw new Exception("Markdown emphasis was not rendered");
                markdown.Width=240;if(!markdown.Text.Contains("保修"))throw new Exception("Markdown resize lost table content");
            }
            string citationId=new string('a',32);
            var chat=MainWindow.FormatChatMessages(new JsonArray(new JsonObject{["role"]="user",["text"]="查找 **literal**"},new JsonObject{["role"]="assistant",["text"]=$"### 结果\n\n** 文件： ** 示例\n\n[[{citationId}:2]] 与 [[{citationId}:2]]\n[[{citationId}:99999999999999999999999]]"}));
            if(chat.References.Count!=1||chat.References[0].Page!=2)throw new Exception("Chat citation deduplication or page validation failed");
            using(var rendered=new MarkdownView{Width=320}){
                rendered.SetMarkdown(chat.Markdown);if(!rendered.Text.Contains("**literal**")||!rendered.Text.Contains("原文 1 · 第 2 页")||rendered.Text.Contains("scanarchive-reference:"))throw new Exception("Chat Markdown lost literal user text or exposed citation URL");
                rendered.Select(rendered.Text.IndexOf("文件：",StringComparison.Ordinal),3);if(rendered.SelectionFont?.Bold!=true)throw new Exception("Spaced Markdown bold label failed");
            }
            int attempts = 0, waits = 0;
            int transferred = Scanner.TransferWithRetry(() =>
            {
                if (++attempts < 3) throw new System.Runtime.InteropServices.COMException("Busy", unchecked((int)0x80210006));
                return 42;
            }, CancellationToken.None, () => waits++);
            if (transferred != 42 || attempts != 3 || waits != 2) throw new Exception("Busy retry loses successful page");
            attempts = 0;
            try
            {
                Scanner.TransferWithRetry<int>(() => { attempts++; throw new System.Runtime.InteropServices.COMException("Busy", unchecked((int)0x80210006)); }, CancellationToken.None, () => { });
                throw new Exception("Busy retry should fail after bounded attempts");
            }
            catch (System.Runtime.InteropServices.COMException) { if (attempts != 11) throw new Exception("Unbounded busy retry"); }
            using (var stop = new CancellationTokenSource())
            {
                attempts = 0;
                try
                {
                    Scanner.TransferWithRetry<int>(() => { attempts++; throw new System.Runtime.InteropServices.COMException("Busy", unchecked((int)0x80210006)); }, stop.Token, () => stop.Cancel());
                    throw new Exception("Busy retry ignored stop request");
                }
                catch (OperationCanceledException) { if (attempts != 1) throw new Exception("Transfer continued after stop request"); }
            }
            attempts = 0;
            try
            {
                Scanner.TransferWithRetry<int>(() => { attempts++; throw new System.Runtime.InteropServices.COMException("Paper jam", unchecked((int)0x80210002)); }, CancellationToken.None, () => { });
                throw new Exception("Paper jam should fail immediately");
            }
            catch (System.Runtime.InteropServices.COMException) { if (attempts != 1) throw new Exception("Unsafe retry of paper jam"); }
            using (var body = new Panel { Dock = DockStyle.Fill })
            using (var card = MainWindow.Card("Preview", body))
            {
                foreach (var size in new[] { new Size(800, 1000), new Size(480, 400) })
                {
                    card.Size = size;
                    card.PerformLayout();
                    var title = card.Controls.OfType<Label>().Single();
                    if (body.Top < title.Bottom || body.Bottom > card.ClientSize.Height - card.Padding.Bottom)
                        throw new Exception("Preview title overlaps or clips the document area");
                }
            }
            string image = Path.Combine(folder, "input.bmp");
            using (var bitmap = new Bitmap(600, 900)) { using var g = Graphics.FromImage(bitmap); g.Clear(Color.White); g.DrawString("Archive test", SystemFonts.DefaultFont, Brushes.Black, 30, 30); bitmap.Save(image); }
            using (var first = File.OpenRead(image))
            {
                string acquired = FeederScanner.SavePage(first, folder, 1);
                using var check = Image.FromFile(acquired);
                if (check.Width != 600 || check.Height != 900) throw new Exception("Feeder stream dimensions");
            }
            using (var second = new MemoryStream())
            {
                using (var bitmap = new Bitmap(320, 480))
                {
                    using var g = Graphics.FromImage(bitmap);
                    g.Clear(Color.Blue);
                    bitmap.Save(second, System.Drawing.Imaging.ImageFormat.Bmp);
                }
                string acquired = FeederScanner.SavePage(second, folder, 2);
                using var check = new Bitmap(acquired);
                if (check.Width != 320 || check.GetPixel(10, 10).B != 255) throw new Exception("Feeder page stream mixed or cropped");
            }
            bool invalidPageRejected = false;
            try { using var broken = new MemoryStream([1, 2, 3]); FeederScanner.SavePage(broken, folder, 3); }
            catch (ArgumentException) { invalidPageRejected = true; }
            if (!invalidPageRejected || File.Exists(Path.Combine(folder, "page-0003.bmp"))) throw new Exception("Invalid feeder page accepted");
            var date = new DateTime(2026, 9, 6, 12, 30, 0);
            string pdf = Archive.Save(folder, "PDF", [image, image], 300, date);
            using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import))
            {
                if (document.PageCount != 2) throw new Exception("PDF page count");
                if (Math.Abs(document.Pages[0].Width.Point - 144) > 1) throw new Exception("PDF dimensions");
            }
            using (var rendered = new PdfPreviewDocument(pdf))
            {
                if (rendered.PageCount != 2) throw new Exception("PDF preview page count");
                using var page = rendered.RenderPage(1);
                if (page.Width < 1 || page.Height < 1) throw new Exception("PDF preview render");
            }
            string png = Archive.Save(folder, "PNG", [image], 300, date);
            using (var bitmap = Image.FromFile(png)) if (bitmap.Width != 600) throw new Exception("PNG dimensions");
            string jpg = Archive.Save(folder, "JPEG", [image], 300, date);
            using (var bitmap = Image.FromFile(jpg)) if (bitmap.Height != 900) throw new Exception("JPEG dimensions");
            string duplicate = Archive.Save(folder, "PDF", [image], 300, date);
            if (duplicate == pdf || !File.Exists(pdf)) throw new Exception("Collision protection");
            if (!pdf.EndsWith(Path.Combine("Inbox", "2026-09-06_12-30-00-000.pdf"))) throw new Exception("Inbox archive path");
            if (!duplicate.EndsWith("2026-09-06_12-30-00-000_002.pdf")) throw new Exception("Sequential collision suffix");
            if (Archive.Clean("CON") != "_CON" || Archive.Clean("..") == "..") throw new Exception("Unsafe path");
            if (Scanner.NormalizeValue(150, 2, 0, 0, 1, [100, 200, 300]) != 200) throw new Exception("WIA list normalization");
            if (Scanner.NormalizeValue(301, 1, 100, 600, 10, []) != 300) throw new Exception("WIA range normalization");
            if (Scanner.NormalizeValue(5, 3, 0, 0, 1, [1, 2, 4]) != 5) throw new Exception("WIA flag normalization");
            if (Directory.EnumerateFiles(folder, "*.partial", SearchOption.AllDirectories).Any()) throw new Exception("Partial files remain");
            if (Scanner.MaximumValue(1, 1, 2550, 1, []) != 2550) throw new Exception("WIA maximum extent");
            if (Scanner.MaximumValue(1, 1, 100, 8, []) != 97) throw new Exception("WIA maximum step alignment");
            if (Scanner.MaximumValue(2, 0, 0, 1, [3508, 4200, 3300]) != 4200) throw new Exception("WIA maximum listed extent");
            bool unsupportedExtentRejected = false;
            try { Scanner.MaximumValue(0, 0, 0, 1, []); }
            catch (InvalidOperationException) { unsupportedExtentRejected = true; }
            if (!unsupportedExtentRejected) throw new Exception("WIA missing extent bounds");
            var devices = Scanner.Devices();
            File.WriteAllText(report, $"PASS: multipage PDF, PDF page rendering/navigation data, page dimensions, PNG, JPEG, date folders, timestamp filenames, sequential collision suffix, atomic saves, WIA value normalization.\nWIA scanners: {string.Join(", ", devices.Select(d => d.Name))}\nTest artifacts: {folder}");
        }
        catch (Exception ex) { File.WriteAllText(report, "FAIL: " + ex); Environment.ExitCode = 1; }
    }
}
