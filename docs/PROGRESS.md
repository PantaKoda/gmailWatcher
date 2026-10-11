# Progress

Working notes for whoever (human or agent) continues this build. Keep it accurate; it is the first thing to read.

## Product in one paragraph

**Gmail Watcher**: a Windows desktop app (Avalonia, .NET 10) that downloads one or more Gmail accounts into a local, read-only
backup: SQLite (authoritative, with full-text search) plus files (.eml and attachments) plus DuckDB (derived analytics), so the
user can verify the backup and then empty their Gmail. Looks and updates like the sibling app `repo-watch`
(HUD theme, accent presets, GitHub-release updates).

## Decisions that shape everything (and why)

- **Read-only scope only** (`gmail.readonly`): the app cannot delete or change anything in Gmail. The user deletes mail themselves, after verifying.
- **Gmail API, not IMAP**: no bandwidth cap, history-based incremental sync, labels. Quota is the limit: `messages.get` = 20 units, 6,000 units/min/user
  (Google docs, Oct 2026) ≈ 300 messages/min ≈ 18k/hour. Big first syncs take hours; they are resumable.
- **Archive is append-only.** A message Gmail no longer has is only marked `remote_deleted_at`. Emptying Gmail must never empty the archive (tested).
- **SQLite authoritative, DuckDB derived**: DuckDB is rebuilt from SQLite if missing/damaged; it is opened only while working so external tools can open the file.
- **Raw .eml is the backup** (byte-identical to Gmail's raw, SHA-256 recorded); attachments are also extracted (marked "from the internet" via Zone.Identifier).
- **Verification** = (every ID Gmail lists is archived) AND (every message file still matches its recorded size + SHA-256) AND (SQLite quick_check). Only then "Verified". The extracted attachment files are convenience copies of what the .eml already holds: if one is missing, changed or blocked by an antivirus while its .eml is intact, that is a *note* (`VerificationReport.Notices`), not a failure; if the .eml is damaged too, it is a problem.
- Hand-rolled Google OAuth (loopback + PKCE) and Gmail REST client; no Google SDK.
- The only destructive path in the app is "Delete from this PC" (typed `DELETE`); it never touches Gmail.

## Layout

```
src/GmailWatcher.Core      models, contracts (IMailbox, ICredentialStore...), options, settings codec, update models (no UI/OS deps)
src/GmailWatcher.Google    OAuth + Gmail REST + QuotaGovernor
src/GmailWatcher.Archive   SQLite, files, ingest (MimeKit), SyncEngine, Verifier, DuckDB mirror, insights, search, in-memory fake mailbox
src/GmailWatcher.Desktop   Avalonia UI, composition root, updates, platform adapters
tests/*                    xunit.v3 on Microsoft.Testing.Platform:  dotnet test --project tests/<name>   (no -nologo; not `dotnet test <path>`)
```

## Status

| Stage | State |
| --- | --- |
| 1. Scaffold (props, packages, slnx) | done |
| 2. Core contracts/models | done |
| 3. Archive engine | done (sync, verify, repair, DuckDB, insights, search) |
| 4. Google project (OAuth/Gmail REST/quota) | done: built by a subagent, reviewed and integrated |
| 5. Desktop app (theme port, shell, views, services) | done: accounts list, details (overview/insights/browse), connect, settings, update, setup dialogs |
| 6. Updates (port from repo-watch), release scripts, CI | done: `scripts/publish-windows.ps1`, `.github/workflows/{ci,release}.yml` |
| 7. Docs (README, google-cloud-setup, architecture, updates), CHANGELOG | done |
| 8. End-to-end check with `--demo`, screenshots, publish script run | done (see below) |

Tests: **752**, all passing (Core 78, Google 292, Archive 133, Desktop 249). The full suite was run repeatedly back to back to
shake out flaky tests; the ones found were test-harness races (see Gotchas), not product bugs, and are fixed.

## Verified vs not verified

Verified:
- Everything in the Archive project against the in-memory mailbox (ingest, sync, verify, repair, DuckDB, search, append-only behaviour).
- The Google client's sync path end to end against a fake Gmail HTTP server (`RestSyncEndToEndTests`): list, get raw, history, labels, 404/400 history expiry.
- The UI: every window is rendered headless with demo mail in both themes (`artifacts/screenshots`, written by the Desktop tests) and was looked at.
  Light-theme status colours were darkened to reach 4.5:1 contrast on both surface stops (tested).
- The published build: `scripts/publish-windows.ps1` produces the zip + sha256 + manifest; the extracted exe starts and shows the demo accounts (`GmailWatcher.exe --demo`).

**Not verified, cannot be without the user's Google OAuth client:** real Google sign-in, real Gmail API behaviour, real quota/throttling.
The Google client is tested against scripted HTTP fixtures built from Google's documentation only. Also not verified: in-app update against a real GitHub release
(the logic is ported from repo-watch and unit-tested, but the repository `PantaKoda/gmailWatcher` has to exist and have a `vX.Y.Z` release first).

## Not done / left to the user

- The first release is **0.1.1**. A `v0.1.0` tag exists on GitHub (it points at the initial commit) but was never released: its release build caught a real bug
  (see Gotchas) and the fix shipped as 0.1.1 rather than moving a published tag. Don't build on `v0.1.0`; it may be deleted when convenient.
- Releases: push a tag `vX.Y.Z` that matches `<Version>` in `Directory.Build.props` and has a `## [X.Y.Z]` section in `CHANGELOG.md`;
  `.github/workflows/release.yml` builds, tests and publishes the zip that the in-app updater reads.
- Before publishing anything from this repo: never commit `client_secret*.json`, `gmailwatcher.config.json`, archives, or `.duckdb` files (all git-ignored),
  and commit with a GitHub noreply address, not a personal email.
- The exe is unsigned (SmartScreen will warn). Windows only.
- A Google Cloud OAuth client (guided in the app and in `docs/google-cloud-setup.md`). Publish the consent screen "In production" to avoid 7-day refresh-token expiry.
- Back up the archive folder: after Gmail is emptied it is the only copy.

## Gotchas learned

- `dotnet test --project X` / `--solution Y` (MTP). `-nologo` is rejected by the test app. xUnit1051 is an error: pass `TestContext.Current.CancellationToken`.
- MimeKit 4.x has no `HtmlToText`; `HtmlText.Extract` in Archive does it. NonBacktracking regex: no back-references.
- Text MIME parts are CRLF-canonicalized: decoded size can differ from the bytes you wrote.
- After `Repair`, history must be cleared so the next sync is a full pass (done in `ArchiveMaintenance`).
- Never `SqliteConnection.ClearAllPools()` in code that tests run in parallel: use `ArchiveDatabase.ReleaseConnections()`.
- Desktop view-model tests use `PumpDispatcher` and `TestKit.UntilAsync/WaitIdleAsync`; do not assert on state a background thread can still change.
- FTS5 search is prefix-based: tests must not rely on random words.
- Background work that posts to the UI must be guarded by a version: on a loaded machine (GitHub's runner) a cancelled search can start *after* its
  successor finished. `BrowseViewModel.RunAsync` bails out if cancelled and only the newest search touches `IsSearching`
  (test: `A_search_that_lost_the_race_cannot_leave_the_spinner_on`). This was found by the first release build, not locally.
- Avalonia compiled bindings: a nested `x:DataType` makes same-element `DataContext`/`IsVisible` bindings resolve against the child context; wrap in a Panel/ContentControl.
- Style the destructive button as `Button.accent.danger`; a bare `Button.danger` also catches icon buttons (the window Close button turned red once).
- .NET only knows UTF-8/16/32, ASCII and Latin-1. Mail in iso-8859-7, windows-1253, windows-1251, shift_jis... needs `CodePagesEncodingProvider` registered, or it decodes to Latin
  letters ("Êáëçìýñá"). `EncodingSupport` (module initializer in the Archive assembly) does it. The first release lacked it (found when the user saw gibberish Greek mail);
  `TextRefresher` re-reads the saved .eml files once per account (`meta` key `text_version:<account>`, `TextRefresher.CurrentVersion`) at the start of the next sync. Bump
  `CurrentVersion` whenever decoding improves again. Archive-only (disconnected) accounts are not refreshed, because the refresh runs inside a sync.
- Avalonia 12: `ContextMenu.PlacementTarget` is null; find the owner with `FindLogicalAncestorOfType<SelectableTextBlock>()` from the menu item. `SelectableTextBlock` has no built-in menu.
- Python is not installed; use sed/PowerShell/Edit for scripted edits. Escaped backslashes through sed are unreliable: use the editor.
- Bash heredocs containing apostrophes break the Bash tool: write files with the Write tool.
- CI runners have slow disks and the app flushes every file (by design), so syncs in tests can take 10-20x longer there than on a dev PC, especially while the other test
  assemblies run in parallel. Test waits are generous on purpose (`TestKit.UntilAsync` 120 s, `WaitIdleAsync` 240 s): they only matter when something really hangs. The release
  workflow reruns the tests, so a flaky test blocks a release: if CI fails, read the log before re-running, and fix the cause.
- Gmail's `messages.list` can keep naming messages for a while after a bulk deletion (the profile's `messagesTotal` was 389 while the list still had 1,826 ids). Asking for such a
  message answers 404 (`GetMessageAsync` returns null). Sync counts it as vanished; Verify probes up to 40 listed-but-not-archived ids and records the ones Gmail says do not
  exist as notes (`ListedButGone`), not as gaps. More than 40 unarchived ids means the archive is simply behind: sync, then verify again.
- Tray / background (0.2.0): `AppShell` runs with `ShutdownMode.OnExplicitShutdown`; the app ends when the main window really closes (`Quit`, OS shutdown, or closing with "keep running in the tray" off), never when it is merely hidden. `WindowLifetime` holds the close/minimise rules, `TrayStatus` the tooltip and icon, `TrayController` the Avalonia `TrayIcon`, `IStartupRegistration` the HKCU Run entry (`--background` = start hidden). A second launch shows the window (named pipe); a second `--background` launch exits silently.
- How the tray was verified: launch the built exe with `GMAILWATCHER_DATA_DIR` pointing at a scratch folder and `--demo`, `CloseMainWindow()` (it must stay running with no window), launch again (window returns), launch with `--background`. Windows records every notification-area icon under `HKCU\Control Panel\NotifyIconSettings` (ExecutablePath, InitialTooltip), which is how its creation can be seen from a script; the UI Automation tree does not show icons in the collapsed overflow.
