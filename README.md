# 扫描归档

中文 Windows 桌面扫描程序，使用 WIA 调用扫描仪。本机已检测到 Brother DCP-L2640DW 的 WIA 接口。

## 使用

运行 `dist/ScanArchive.exe`。首次使用时在“设置”中选择扫描仪和归档目录，之后在主页点击“开始扫描”。

- 平台扫描单页；自动进纸器逐页扫描并合并 PDF，缺纸后完成。
- 支持 PDF、PNG、JPEG；150/200/300/600 DPI（依设备驱动支持）；A4、彩色或灰度。
- 主页只保留扫描按钮、扫描预览和文件列表；设备、目录和扫描参数位于独立设置页。
- 归档结构：`归档目录/2026/09/06/2026-09-06_19-30-15-123.pdf`。
- 文件名精确到毫秒；同一时间仍重名时自动追加 `_002`、`_003`，不会覆盖已有扫描件。
- 图片可直接预览；PDF 双击后由系统阅读器打开。
- 在文件列表中右键可删除扫描件；确认后文件移入 Windows 回收站。
- 扫描过程中，主页按钮会变为“当前页完成后停止”，已扫描页面仍会保存。
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

自检生成测试图片，验证多页 PDF、尺寸、PNG/JPEG、日期目录、时间文件名、顺序编号和临时保存，并枚举真实 WIA 设备；不会触发真实扫描。

## 当前范围

当前版本按扫描时间归档，没有文件分类、OCR 内容识别、双面扫描或云备份。归档是保存一份文件，不等于多副本备份。

设备资料：[Brother DCP-L2640DW 驱动](https://support.brother.com/g/b/downloadtop.aspx?c=us&lang=en&prod=dcpl2640dw_us_as)。
