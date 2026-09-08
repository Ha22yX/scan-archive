# Scan Archive

> An efficient, end-to-end Windows solution for turning paper documents into organized digital archives.

Scan Archive is purpose-built for fast, repeatable document scanning and archiving. It brings the entire workflow into one desktop application: scanner control, flatbed or multi-page acquisition, PDF creation, instant preview, page navigation, timestamped naming, and automatic date-based filing.

Designed for high-volume daily use, Scan Archive removes manual filenames and folder decisions from each scan. Place the document, click once, and the application produces an organized digital record that is ready to find, review, and manage.

The application interface is currently in Simplified Chinese.

## Features

- One-click scanning from a clean home screen with a document preview and scan history.
- Single-page flatbed scanning and multi-page automatic document feeder scanning.
- PDF, PNG, and JPEG output in color or grayscale.
- Local PDF rendering with previous-page and next-page navigation.
- Automatic date-based folders and timestamp filenames with collision-safe sequence numbers.
- Right-click deletion to the Windows Recycle Bin after confirmation.
- Scanner, archive folder, resolution, output format, and feeder options on a separate settings page.
- Local-only processing. Scanned documents are not uploaded to an external service.

## Archive Structure

Files are stored under the selected archive folder using the following structure:

```text
Archive folder/
└── 2026/
    └── 09/
        └── 07/
            └── 2026-09-07_09-30-15-123.pdf
```

Filenames include milliseconds. If two files still receive the same timestamp, the application adds `_002`, `_003`, and subsequent sequence numbers instead of overwriting an existing document.

## Usage

1. Run `dist/ScanArchive.exe`.
2. Open **设置** (Settings) and choose the scanner and archive folder.
3. Select the resolution, output format, color mode, and flatbed or document feeder mode.
4. Return to **主页** (Home) and click **开始扫描** (Start Scan).

During a multi-page scan, the scan button changes to **当前页完成后停止** (Stop After Current Page). Pages already scanned are still archived safely.

The Brother DCP-L2640DW detected during development supports 100, 200, and 300 DPI through its current Windows WIA driver.

## Settings and Recovery

Application settings are stored in:

```text
%LOCALAPPDATA%/ScanArchive/settings.json
```

If scanning or archival fails, successfully acquired source pages remain in:

```text
%LOCALAPPDATA%/ScanArchive/Pending/
```

The error message displays the exact recovery folder. Temporary pages are removed automatically after a successful archive operation.

## Build

Requirements:

- Windows 10 or Windows 11
- .NET 10 SDK
- A WIA-compatible scanner driver

```powershell
dotnet build ScanArchive -c Release
dotnet publish ScanArchive -c Release -r win-x64 --self-contained true -o dist
```

The published `dist` directory includes the .NET runtime and the native PDF rendering library. Copy the complete directory when distributing the application.

## Verification

```powershell
Start-Process ./dist/ScanArchive.exe -ArgumentList '--self-test' -Wait
Get-Content ./dist/self-test.txt
```

The self-test generates temporary images and verifies multi-page PDF creation and rendering, page dimensions, PNG and JPEG output, date folders, timestamp filenames, collision sequence numbers, atomic saves, and WIA device enumeration. It does not start a physical scan.

## Current Scope

Scan Archive organizes documents by scan time. OCR, content-based classification, duplex scanning, and cloud backup are not currently included. Archival creates one local copy and should not be treated as a multi-copy backup strategy.

Brother device support: [DCP-L2640DW downloads and drivers](https://support.brother.com/g/b/downloadtop.aspx?c=us&lang=en&prod=dcpl2640dw_us_as).
