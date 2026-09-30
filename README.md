<h1 align="center">Scan Archive</h1>
<p align="center">Scan once. Keep the original. Let your document secretary make it findable.</p>
<p align="center"><strong>English</strong> · <a href="README.zh-CN.md">简体中文</a></p>
<p align="center">Windows · WIA / WIA 2.0 · OpenAI · Local document library</p>

![The capture, analysis, organization and retrieval workflow](docs/readme-hero.svg)

## The application

![Native Windows workspace with synthetic demonstration documents](docs/screenshots/native-workspace.png)

![Web document library with synthetic test data](docs/screenshots/web-library.png)

A native Windows workspace brings scanning, the document list, page preview, AI summaries and secretary conversations into one application. Select a document, read it, ask about the current page, and follow an answer’s citations without leaving the workspace. Processing history, reversible file management and all AI settings are native screens too. The interface is currently in Simplified Chinese.

The web dashboard is an optional remote entrance to the same library and conversations. The desktop uses current-user Windows IPC directly; it does not embed a browser and does not require a web login or WebView2.

## Why this exists

Paper piles up. Saving it takes effort; finding it again takes even more. I wanted to put a document in my printer's scanner, click once, and keep a useful digital copy without deciding on a filename and folder every time.

Scan Archive connects capture to a personal document library. An AI analysis step reads each page and saves detailed metadata before a separate secretary agent organizes the file. The ambition is a private knowledge base where a sentence can lead back to the actual document and page, while the original scan remains available.

## Run on Windows

Requirements: Windows x64, .NET 10 SDK to build, a WIA-compatible scanner driver, and an OpenAI API account for analysis and agent features. The published application includes its .NET runtime. Scanning and preview work without an API key.

```powershell
git clone https://github.com/Ha22yX/scan-archive.git
cd scan-archive
.\scripts\publish.ps1
.\dist\ScanArchive.exe
```

1. In the desktop settings, select your scanner, archive root, resolution and flatbed/feeder mode.
2. If you want remote access, open **http://localhost:5278** on the service computer and create a password of at least 10 characters. The native desktop works without this step.
3. Enter your OpenAI API key in desktop or web settings. The desktop settings expose analysis/agent and embedding models, request limits, agent step limits and long-term instructions. Default models are `gpt-6-astra` and `text-embedding-3-large`; choose models your account supports.
4. Enable automatic organization and choose a daily wake time (default **05:00**, using Windows local time).
5. Scan. The desktop saves the capture to `Inbox`, then submits a durable scan event directly to the core over a Windows named pipe restricted to the current user. The core acknowledges the document ID, analyzes every page, builds the index, and queues the secretary. Desktop and web lists read the same database and update automatically.

The core starts with the desktop application. An interrupted handoff stays in the desktop outbox and is retried while the application is open, including after restart. Both interfaces use the same archive root; changing the web configuration cannot silently send the next scan to an old desktop folder. The desktop file list shows analysis/organization states, not directory contents. Right-click deletion moves a record into the application recycle bin, hides it in both interfaces and search, and retains its bytes; restore it from the native Processing History / Recycle Bin or web Activity. Files under active processing cannot be trashed until that task ends. The service starts with the desktop application. You can also run `scripts/start-secretary.ps1`. Closing the scanner window leaves the service running. To opt into Windows sign-in startup, run `scripts/enable-startup.ps1`; use `-Disable` to remove it. The computer must be awake and the archive drive available. A missed daily review is caught up once when the service resumes; this is not a wake-from-sleep task or a pre-login Windows service.

### Working in the desktop app

- **Capture and browse:** the scan button stays available across the app. The library shows processing states, category/status filters and batches of 60 documents. Background refresh preserves the selected document and preview page.
- **Read and find:** search combines exact text, full-text and semantic retrieval. Selecting a match opens its page. Preview supports previous/next, direct page number entry and opening the original. PDF rendering runs off the UI thread.
- **Readable document details:** native Markdown rendering supports headings, emphasis, lists, quotes, code and tables. New AI summaries use structured Markdown. Use **Markdown 整理** on an existing document to reformat its summary without rescanning; before/after revisions are preserved in SQLite and JSON metadata. Right-click the detail pane to copy Markdown. HTML and remote images are not executed or loaded.
- **Ask your secretary:** use the side panel, optionally attach the selected document/page, resume existing conversations and click source references to preview them. Pending/running jobs remain visible while work continues in the background.
- **Manage and recover:** change titles/categories, lock manual choices, unlock them for the agent, retry analysis, request organization, undo moves and restore trashed records.
- **Shortcuts:** `Ctrl+F` focuses search, `F5` refreshes, `Ctrl+Enter` sends a chat message, and `Delete` on the document list opens a deletion confirmation.

### Local network access

Use `http://<computer-IP>:5278` from another device on the same LAN and sign in with the same password. The service binds to all interfaces by default. If blocked, run `scripts/enable-lan.ps1` from an **administrator PowerShell**; it permits TCP 5278 only on private networks from the local subnet. Do not forward this port to the internet. The default transport is HTTP, intended for a trusted private network; remote access requires an authenticated HTTPS gateway configured separately.

## From paper to a searchable library

| Stage | Behavior |
| --- | --- |
| Capture | Flatbed or native WIA 2.0 feeder; PDF/PNG/JPEG; driver-reported maximum scan area; timestamped capture name |
| Page analysis | OpenAI vision transcribes visible text and records detailed summaries, subjects, entities, dates, exact numbers, bilingual keywords and readability uncertainty |
| Document analysis | All pages contribute to a comprehensive document overview; long documents use grouped outlines rather than dropping later pages |
| Durable metadata | SQLite plus per-document JSON, including scan time, content hash, stable ID, original location, per-page analysis, model and analysis time |
| Organization | A separate tool-using agent chooses semantic folders and titles, can split independent documents into separate PDFs, and retains the original |
| Retrieval | Exact matches, SQLite FTS5 with Chinese bigrams, page-level overlapping chunks and OpenAI embeddings, combined with reciprocal-rank fusion |
| Agent search | Multiple complementary queries, reading the matching source pages, and clickable document/page citations; known analysis backlogs are disclosed |
| Daily review | Reviews library structure, names, index coverage and stored preferences; can reorganize categories or repair missing vectors |

The agent can inspect and search documents, read pages, create categories, move/rename files, merge categories, split PDF ranges, queue analysis, repair indexes, remember preferences and undo moves. Its tools operate within the configured library. Manual categorization locks a document against agent moves. File moves are journaled and reversible; source bytes are retained. Document contents are treated as evidence, not instructions.

### Storage and traceability

When archived child documents cover every page of a split batch exactly once, the batch leaves the normal library, search results and document counts automatically. Incomplete or overlapping splits keep their source visible. Original scans remain available through **View original scan** on a child document for traceability; deleting a child makes the source batch visible again.

Background processing defaults to **3 documents at once**, with **2 concurrent page analyses per document**. Both limits are adjustable in desktop and web settings. Completed page analyses are checkpointed and reused after interruption or splitting. OpenAI requests share an eight-request concurrency cap. A separate, serial agent queue handles organization and conversations alongside analysis, keeping filesystem changes ordered. Actual throughput depends on document size, model latency and API rate limits.

```text
Archive root/
  Inbox/                       # Newly captured/imported files
  Library/<AI categories>/     # Organized files; stable ID suffix avoids collisions
  .scanarchive-originals/      # Original bytes identified by SHA-256
  .scanarchive-metadata/       # One comprehensive JSON file per document ID
%LOCALAPPDATA%/ScanArchive/Secretary/
  library.db                   # Search index, metadata, jobs, conversations and audit trail
  settings.json                # Non-secret configuration
  openai.secret                # API key encrypted for the current Windows account
  outbox/                      # Desktop-owned capture events; removed only after acknowledgement
```

The scan timestamp is separate from any date found in the document. Split children inherit that timestamp and retain the parent ID and original page range. There is no automatic archive-directory crawling. Existing files enter through the native file picker, explicit web upload or the one-time manual legacy import command; when no capture event exists, their filesystem creation time is used as a fallback, which may be inaccurate after copying. Exact byte duplicates share one document record; individual capture events retain their own scan time, device and source. Events are idempotent even after an agent renames the file. Metadata is available in the document details and as JSON beside the library; page checkpoints survive API failures.

**Back up both the archive root and the local Secretary data directory**, with the service stopped for a consistent database copy. Keeping originals on the same drive is recovery protection, not an independent backup. DPAPI secrets are bound to the Windows account: configure the API key again after migration. JSON exports preserve content and provenance, but a complete automated database restore from sidecars is not yet implemented.

## Implementation and verification

- Desktop: C# / .NET 10 WinForms, WIA, NAPS2.Wia, PDFsharp and Docnet/PDFium.
- Service: ASP.NET Core, SQLite/FTS5, plain JavaScript/CSS, no separate Node deployment.
- AI: official OpenAI Responses API with structured analysis and a bounded tool loop; embeddings stored with model provenance. `store:false` is used for Responses requests; scan content is still transmitted to OpenAI for processing.
- Reliability: durable job queue, direct idempotent capture handoff, per-page checkpoints, retry/backoff, daily request cap, lexical search when embeddings fail, recoverable move journal and protected original copies.
- Access: password authentication, HTTP-only same-site cookies, same-origin mutation checks, DPAPI-protected key storage. API keys are not returned by settings endpoints.

```powershell
dotnet test ScanArchive.Server.Tests -c Release
.\dist\ScanArchive.exe --self-test
```

Automated tests use synthetic PDFs and a mocked OpenAI transport. Native IPC tests also cover shared conversations, settings validation, reversible management, and browsing while a search is in flight. Mixed-page retrieval tests prevent global document tags from being mistaken for evidence on every page; interrupted index rebuilds retain the previous index. They cover analysis-before-organization, metadata persistence, Chinese/identifier retrieval, embedding outages, scan provenance after splitting, moves/undo, path boundaries, verified citations and daily scheduling. These tests do **not** measure real-model OCR or retrieval quality. Real API behavior, cost and document accuracy must be evaluated with your own key and representative scans. Handwriting, clipping and tiny text can still be misread; review important results against the page preview.

Page indexes use only that page’s content and analysis. Overall metadata is a low-weight discovery fallback, with an explicit instruction to verify the actual page. Existing indexes upgrade once without repeating page vision analysis.

The semantic search implementation currently scores vectors in process; large libraries will eventually need a dedicated approximate-nearest-neighbor index. A request-count limit is not a dollar spending cap. The agent has a bounded number of tool steps per run, so very large reorganizations may need another wake-up.

## What comes next

- A real-document retrieval evaluation set with measured page-level recall.
- Better local OCR and selective high-resolution rereading for difficult pages.
- Document relationships, duplicate-scan history and richer knowledge-base navigation.
- Faster vector retrieval for large collections, full migration tooling and encrypted off-device backup.

The direction is simple: preserve paper faithfully, understand it usefully, and make the original easy to find again.
