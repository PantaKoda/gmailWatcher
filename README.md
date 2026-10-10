# Gmail Watcher

A Windows desktop app that makes a **complete, local, searchable backup of your Gmail accounts**, so you can check that nothing is missing and then **empty Gmail** (to free its storage) without losing a thing.

- Add as many Gmail accounts as you like. The main window lists them, each with a **Sync** button.
- Every message is saved exactly as Gmail has it (a standard `.eml` file), with its labels, its attachments as separate files, and your mailbox settings (filters, aliases and signatures, forwarding). Spam and Trash included.
- Everything is also stored in **SQLite** (with full-text search) and mirrored into **DuckDB** for fast analytics: who writes to you most, what fills your mailbox, the biggest attachments.
- **Verify** proves the backup is complete and intact *before* you delete anything.
- Looks and updates like [Repo Watch](../repo-watch): the same space-station HUD theme and accent colors, and updates straight from GitHub releases.

Status: version 0.1.2, Windows 10/11 x64. See [Limitations](#limitations-and-what-was-not-verified).

## The safety model, in five lines

1. **Read-only.** Gmail Watcher asks Google for the read-only Gmail permission. It *cannot* delete or change anything in Gmail. You empty Gmail yourself, in Gmail.
2. **Append-only archive.** Mail that disappears from Gmail is only *marked* "no longer in Gmail"; it is never removed from the archive. Emptying your Gmail cannot empty your archive (this is tested).
3. **Verified, not assumed.** *Verify* lists everything Gmail holds right now (Spam and Trash too) and checks every one has an archived copy, then re-reads every saved file against the SHA-256 recorded at download time. Only both together say *Verified*, and the result is dated: mail that arrives later isn't covered, so sync and verify again right before you delete.
4. **Nothing is half-written.** Files are written to a temporary name, flushed to disk and renamed; the database row that points at a file is committed only after the file exists. Stopping at any moment, or a crash, loses at most the batch in flight and the next sync continues.
5. **After Gmail is empty, the archive folder is the only copy.** Back it up like any important folder (another drive or cloud storage you control).

## Quick start

1. Download `GmailWatcher-<version>-win-x64.zip` from the releases page, extract it to a folder of its own (for example `%LOCALAPPDATA%\Programs\GmailWatcher`; **not** into the data folder), and start `GmailWatcher\GmailWatcher.exe`. It isn't code-signed, so Windows SmartScreen may ask you to confirm.
2. **Choose the archive folder** on a drive with plenty of space (mailboxes are often 10–100 GB). Avoid folders that sync to the cloud.
3. **Set up Google sign-in** once: a five-minute, free step in the Google Cloud console that only you can do. The app walks you through it; the details are in [docs/google-cloud-setup.md](docs/google-cloud-setup.md).
4. **Add Gmail account**, sign in in your browser, and allow the read-only permission.
5. Press **Sync**. A big mailbox takes **hours** (see below); you can stop and continue any time.
6. Open **Details → Overview** and press **Verify now**. When it says **Verified**, follow the *Before you empty Gmail* steps shown there.

Want to look around first? Start `GmailWatcher.exe --demo` (or press *Explore with demo data* on the welcome screen). It uses made-up mail in its own folder; no Google account is involved.

## What the window shows

- **Accounts:** per account the messages, size and attachments archived, when it last synced, how it is doing right now (progress, estimated time left) and whether it is verified.
- **Details → Overview:** the verification result in plain words, what is archived, the exact steps for emptying Gmail, the archive folder, *Re-check everything*, *Disconnect*, and *Delete from this PC* (typed confirmation).
- **Details → Insights:** analytics computed by DuckDB.
- **Details → Browse:** search all mail (`invoice`, `from:ann`, `subject:"quarterly report"`, `file:budget`), filter by label or attachments, read messages as plain text (copy the text or the whole message with one click, or select part of it), open or reveal attachments.
- **Settings:** archive and Google info, optional automatic syncing while the app is open (off by default), theme, accent, density, motion, updates, diagnostics.

## How long does the first sync take?

Google's documented limit is **6,000 quota units per minute per user**, and downloading one message costs **20** units. That is about **300 messages a minute**, roughly 18,000 an hour, whatever the size. A 50,000-message mailbox takes around three hours; a 300,000-message one the better part of a day. Gmail Watcher paces itself to stay under the limit (default 5,000 units/min) and backs off when Google pushes back. Several accounts download in parallel (two at a time), because the limit is per account. The estimate shown while syncing adapts to the real speed. The first sync is the slow one: later syncs read Gmail's change history and fetch only what is new.

## Where things are

```
<archive folder>\                       you chose it on first run
    archive.db                          SQLite: the authoritative record + search index
    analytics.duckdb                    DuckDB: derived analytics (rebuilt from archive.db if missing or damaged)
    accounts\<address>\mail\2024\05\<id>.eml            the complete message, byte-identical to Gmail's
    accounts\<address>\attachments\2024\05\<id>\<file>  attachments as separate files (marked "from the internet")
%LOCALAPPDATA%\GmailWatcher\            settings.json, gmailwatcher.config.json, logs\, updates\, diagnostics\
Windows Credential Manager              the Google refresh token of each account (never in files)
```

You can open `analytics.duckdb` with the DuckDB command line, Python or DBeaver. The app holds the file open only while it is updating it. Tables: `accounts`, `messages`, `message_labels`, `attachments`, `recipients`.

## Configuration

Settings are read from `appsettings.json` (next to the exe), then `%LOCALAPPDATA%\GmailWatcher\gmailwatcher.config.json`, then environment variables `GMAILWATCHER__Section__Key`. The app writes the archive folder and Google client into the user file itself.

| Key | Default | Meaning |
| --- | --- | --- |
| `Archive:Directory` | chosen on first run | Where the archive lives. |
| `Google:ClientId` / `Google:ClientSecret` | set in the app | Your Google OAuth client ([guide](docs/google-cloud-setup.md)). |
| `Sync:Workers` | 6 | Messages downloaded at once per account (1–16). |
| `Sync:QuotaUnitsPerMinute` | 5000 | What one account may spend of Google's 6,000/min. |
| `Sync:MinFreeDiskMegabytes` | 1024 | A sync stops cleanly below this much free space. |
| `Sync:ExtractAttachments` | true | Also save attachments as separate files (the `.eml` always contains them). |
| `Updates:Repository` | `PantaKoda/gmailWatcher` | Whose GitHub releases are updates. Empty turns updates off. |
| `Updates:CheckIntervalHours` | 24 | 0 = only when asked. |
| `GMAILWATCHER_DATA_DIR` (env) | `%LOCALAPPDATA%\GmailWatcher` | Where settings and logs live. |

To move the archive: close the app, move the folder, then change `Archive:Directory`.

## Updates

Gmail Watcher checks the public GitHub releases of `Updates:Repository` once a day, never downloads on its own, and shows what changed before you choose **Install update**: the zip is verified against the release's SHA-256 file, swapped in, and the previous version is kept for a manual rollback. Your archive and settings are never touched. Details and the maintainer's release steps: [docs/updates.md](docs/updates.md).

## Build, test, run

Requires the .NET SDK pinned in `global.json` (10.0.x).

```bash
dotnet restore GmailWatcher.slnx --locked-mode
dotnet build GmailWatcher.slnx -c Release
dotnet test --solution GmailWatcher.slnx -c Release
dotnet run --project src/GmailWatcher.Desktop -- --demo
pwsh scripts/publish-windows.ps1        # the release zip + SHA-256 in artifacts/release
```

Tests run on xunit v3 with Microsoft.Testing.Platform: use `dotnet test --solution …` or `dotnet test --project tests/<name>`. The UI tests render the real windows headlessly and write screenshots to `artifacts/screenshots`. Design and internals: [docs/architecture.md](docs/architecture.md).

## Privacy

Gmail Watcher has no telemetry and no server. It talks to Google (`accounts.google.com`, `oauth2.googleapis.com`, `gmail.googleapis.com`) to sign in and read your mail, and to `github.com` / `api.github.com` to check for and download updates. Your mail never leaves your PC. Logs contain no message content, subjects, addresses or tokens; *Export diagnostics* redacts them a second time.

## Limitations and what was not verified

- **Real Google was not exercised.** Signing in, the Gmail API and Google's real throttling can only be tested with *your* OAuth client, which doesn't exist until you create it. The sync engine, storage, verification, analytics, search and UI are covered by automated tests against a faithful in-memory Gmail; the Google client is tested against scripted HTTP responses written from Google's documentation. Expect to find a rough edge or two on first contact with the real thing, and please keep the first verification before deleting anything.
- Windows 10/11 x64 only for now. The executable is not code-signed.
- It backs up **mail** (and mailbox settings). Not contacts, calendar, Drive, Photos or chat history.
- Mail that arrives after a verification isn't covered by it; Gmail's history can expire (then a full pass finds what is missing).
- Chat messages and other items the Gmail API doesn't list can't be archived.
- No tray icon or start-at-login: syncing happens while the window is open.
- Search results are the newest-first matches; there is no relevance ranking.

## License

Gmail Watcher is released under the [MIT License](LICENSE): you may use, change and share it, as long as the copyright notice stays with it. It comes with no warranty.
The open-source packages it is built on are listed, with their licenses, in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
