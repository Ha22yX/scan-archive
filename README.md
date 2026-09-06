# 扫描归档

中文 Windows 桌面扫描程序，使用 WIA 调用扫描仪。本机已检测到 Brother DCP-L2640DW 的 WIA 接口。

## 使用

运行 `dist/ScanArchive.exe`。选择扫描仪、归档目录和分类，将文件放在玻璃平台或自动进纸器，点击“开始扫描并归档”。

- 平台扫描单页；自动进纸器逐页扫描并合并 PDF，缺纸后完成。
- 支持 PDF、PNG、JPEG；150/200/300/600 DPI（依设备驱动支持）；A4、彩色或灰度。
- 归档结构：`归档目录/2026/09/06/合同/123000_123_扫描文件_随机标识.pdf`。
- 分类可以直接输入。文件名包含时间与随机标识，避免覆盖。
- 支持按文件名、日期、分类搜索，图片预览，双击打开文件。PDF 由系统阅读器打开。
- 点击停止会等当前页完成，并保存已扫描的页面。
- 设置保存在 `%LOCALAPPDATA%/ScanArchive/settings.json`，不会进入 Git。
- 扫描或保存失败时，已获取的原始页面保存在 `%LOCALAPPDATA%/ScanArchive/Pending/`，错误提示显示具体路径。可手动恢复；归档成功后清除本次缓存。

## 构建

需要 .NET 10 SDK、Windows 和扫描仪 WIA 驱动。

```powershell
dotnet build ScanArchive -c Release
dotnet publish ScanArchive -c Release -r win-x64 --self-contained true -o dist
```

发布目录内置 .NET 运行时。分发时复制整个 `dist` 目录。

## 验证

```powershell
Start-Process ./dist/ScanArchive.exe -ArgumentList '--self-test' -Wait
Get-Content ./dist/self-test.txt
```

自检生成测试图片，验证多页 PDF、尺寸、PNG/JPEG、日期分类路径、防覆盖、文件名安全和临时保存，并枚举真实 WIA 设备；不会触发真实扫描。

## 当前范围

首版按日期和手动分类归档，没有 OCR 内容识别、自动语义分类、双面扫描或云备份。归档是保存一份文件，不等于多副本备份。真实纸张扫描需要放入纸张后在程序中验证。

设备资料：[Brother DCP-L2640DW 驱动](https://support.brother.com/g/b/downloadtop.aspx?c=us&lang=en&prod=dcpl2640dw_us_as)。
