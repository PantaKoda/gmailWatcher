# Changelog

What changed in each Gmail Watcher release. The section for a version becomes its GitHub release notes, which the app shows in its update window, so write for people using the app.

## [Unreleased]

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
