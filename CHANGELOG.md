# Changelog

What changed in each Gmail Watcher release. The section for a version becomes its GitHub release notes, which the app shows in its update window, so write for people using the app.

## [Unreleased]

## [0.2.0] - 2026-10-11

### Added
- **Runs in the background, from the tray.** Gmail Watcher now has a notification-area icon, like Docker Desktop's or Tailscale's. Click it to open the window; right-click it for *Sync all accounts now*, *Settings* and *Quit*. Its tooltip says what the app is doing ("Syncing…", "All accounts verified", "Problems found"), and the icon gets a small dot: white while it syncs or checks, red when something needs you.
- **Closing or minimising the window keeps Gmail Watcher running in the tray** (on by default; *Settings, In the background* turns it off). Together with *Sync connected accounts* (every hour, every 6 hours or daily), your mail is archived without the window open. The first time you close the window, it explains where the app went. Launching Gmail Watcher again brings the window back.
- **Start with Windows** (off by default, *Settings, In the background*): starts Gmail Watcher when you sign in, straight into the tray with no window. It is a per-user Windows start-up entry that you can also see and remove under Windows Settings, Apps, Startup.

### Changed
- Closing the window no longer quits the app unless you turn *Keep running in the tray* off. To quit, use the tray icon's *Quit Gmail Watcher*.

## [0.1.4] - 2026-10-11

### Fixed
- **Verify no longer gets stuck on a message Gmail's list names but Gmail says does not exist.** After you delete a lot of mail in Gmail, its message list can keep naming messages for a while that are already gone. Verify counted such a message as "not downloaded yet", and no sync could ever fix that. Verify now asks Gmail about each listed message that isn't archived: if Gmail says there is no such message, it is shown as a note ("Gmail lists a message that no longer exists") and the account still counts as Verified. A message that really exists and isn't downloaded is still a problem.

## [0.1.3] - 2026-10-10

### Fixed
- **Verify no longer fails because of one blocked or missing attachment copy.** Gmail Watcher saves every message as a complete `.eml` file (which contains the attachments) and also extracts the attachments into separate files. If Windows or your antivirus blocks, removes or damages one of those extra copies ("the file contains a virus or potentially unwanted software"), Verify used to report "Problems found" with nothing you could do. Now, as long as the saved message itself checked out, it is shown as a note ("Notes · nothing is lost") and the account still counts as Verified. A damaged message file is still a problem, and so is a damaged attachment copy of a message whose file is damaged too.

## [0.1.2] - 2026-10-10

### Fixed
- **Mail in older text encodings showed as gibberish** (for example `Êáëçìýñá` instead of `Καλημέρα`). Greek mail sent as ISO-8859-7 or Windows-1253, and likewise Cyrillic, Central European and Asian mail in older encodings, is now read correctly in the message view, the message list and search. Mail you already downloaded is repaired automatically at the start of the next sync ("Improving the text of saved mail"): the saved `.eml` files were never affected, only the subjects, sender names and search entries made from them.

### Added
- **Copy from the message view:** *Copy text* and *Copy all* buttons, selectable subject and sender lines, and a right-click menu (Copy, Select all, Copy whole message) on the text.
- The MIT license and a list of the open-source packages Gmail Watcher uses now ship in the download (`LICENSE`, `THIRD-PARTY-NOTICES.md`).

## [0.1.1] - 2026-10-10

First release.

### Added
- **A complete, local copy of your Gmail.** Add one or more Gmail accounts and press **Sync**: every message is downloaded exactly as Gmail has it (a standard `.eml` file you can open in any mail program), with its labels, its attachments as separate files, and your mailbox settings (filters, aliases and signatures, forwarding). Spam and Trash are included.
- **Verify before you delete.** *Verify* checks that every message Gmail lists is in the archive and that every saved file is still intact (size and SHA-256), and tells you plainly when it is safe to go on. Mail deleted from Gmail later stays in the archive.
- **Read-only by design.** Gmail Watcher asks Google only for read-only access. It cannot send, change or delete anything in Gmail; you empty Gmail yourself once the archive is verified.
- **Resumable and polite.** A big first download takes hours because Google limits how fast mail can be fetched. Stop any time: what was downloaded is kept and the next sync continues. Later syncs only fetch what's new.
- **Search and browse** the archive: full-text search across subjects, senders, bodies and attachment names, label and attachment filters, a plain-text preview (HTML is never rendered), and safe handling of attachments.
- **Insights** powered by DuckDB: mail per month, who writes to you most, what fills your mailbox, the largest attachments, attachment types, labels and duplicate attachments. The same data is in `analytics.duckdb` for your own queries.
- **Looks like Repo Watch:** the same space-station HUD, with Light, Dark and system themes, accent colors, comfortable or compact density, and motion that only runs while something is working.
- **Updates from GitHub releases.** Gmail Watcher checks once a day. When a newer version exists an **Update** button appears with everything that changed; installing downloads it, checks it against its published checksum, swaps it in and restarts. Your archive and settings are untouched and the previous version is kept for a manual rollback.
- **Demo mode** (`GmailWatcher.exe --demo`) with made-up mail in its own folder, to try everything without a Google account.
