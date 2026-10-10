# Architecture

Gmail Watcher is a Windows-first Avalonia desktop app on .NET 10. The design question that shaped everything: *how do you let someone delete their whole mailbox with confidence?* The answers are: never be able to hurt Gmail, never throw anything away locally, and prove completeness instead of asserting it.

## Projects

| Project | Owns | Depends on |
| --- | --- | --- |
| `GmailWatcher.Core` | Models and contracts (`IMailbox`, `ICredentialStore`, sync/verification results), configuration and its validation, settings codec, update models. No UI or OS code. | none |
| `GmailWatcher.Google` | Google OAuth (loopback + PKCE), the Gmail REST client, the quota governor. Hand-rolled on `HttpClient`; no Google SDK. | Core |
| `GmailWatcher.Archive` | SQLite store, files on disk, MIME ingest, `SyncEngine`, `Verifier`, repair, DuckDB mirror, insights, search, and an in-memory fake Gmail (tests and demo mode). No UI. | Core |
| `GmailWatcher.Desktop` | Avalonia views/view models, services (`SyncService`, `AccountService`…), composition root, update installer, platform adapters (Credential Manager, Explorer, browser). | all |

`IMailbox` is the seam between "Gmail" and everything else. The engine, verifier and UI only ever see `IMailbox`; production wires `GmailRestMailbox`, tests and demo mode wire `InMemoryMailbox`. That is why the whole pipeline (real SQLite, real files, real DuckDB, real sync engine) is exercised in tests without Google.

## Data model

SQLite (`archive.db`) is authoritative. DuckDB (`analytics.duckdb`) is derived and rebuildable. Files hold the bytes.

- `accounts`: one row per mailbox; connection state (`connected`, `reconnect_required`, `archive_only`), the Gmail history ID to continue from, last sync/verification results.
- `messages`: one row per Gmail message: ids, dates, subject, sender, size, path and SHA-256 of the `.eml`, attachment counts, `remote_deleted_at`, and `rev`.
- `message_labels`, `message_addresses`, `attachments`: normalized detail. `labels`, `account_settings`: mailbox structure and settings (as the API's JSON).
- `message_fts`: an FTS5 index (contentless; its rowid is `messages.pk`).
- `rev`: a counter bumped once per write transaction; every row a transaction touches carries it. The DuckDB mirror copies "rows with rev greater than the last one I saw", which makes updates incremental and idempotent. An account's rows are written by one sync at a time, so no transaction can commit "behind" a revision the mirror already passed.

Files: `accounts/<address>/mail/yyyy/MM/<gmail id>.eml` is Gmail's raw message byte for byte (the backup itself). Attachments are *also* extracted to `attachments/yyyy/MM/<id>/<name>` for convenience, with the NTFS `Zone.Identifier` marker so Windows treats them as downloaded from the internet. Names from mail are untrusted: `ArchiveLayout` is the only place that turns them into paths, keeps only the leaf of anything path-like, strips characters Windows rejects, avoids reserved device names, and verifies the result stays inside the account folder.

## Sync

`SyncEngine.RunAsync` per account:

1. `getProfile`. The address must match the archive's account, otherwise the sync stops before writing anything (a different Google account was chosen).
2. Labels and mailbox settings are saved (settings failures are non-fatal).
3. **Incremental** when the account finished a full pass before and has a history ID: `history.list` since that ID gives added/deleted messages and label changes in order. New messages are downloaded, deletions are only *marked*, label changes are applied to archived messages. If Gmail no longer has that history (`404`) the engine falls back to the full pass.
4. **Full pass** otherwise: list every message ID (`includeSpamTrash=true`, newest first), download those the archive lacks, mark archived messages Gmail no longer lists, and (if mail was already archived) reconcile each label's membership with `messages.list?labelIds=`. The history ID saved afterwards is the one read *before* listing, so mail that arrived during the pass is picked up next time.
5. **Downloading** is a bounded pipeline: one producer, N workers (`GetMessage` + ingest to disk), one database writer committing batches of up to 50. Backpressure comes from bounded channels. A message that vanished between listing and download is counted, not fatal. The writer ignores cancellation on purpose: messages already written to disk are committed even when the sync was stopped.
6. Results: `Completed` advances the history ID and (for a full pass) marks the initial sync complete. `Cancelled`, `Failed`, `QuotaExhausted`, `DiskFull` and `ReconnectRequired` keep everything downloaded and leave the position unchanged, so the next sync diffs and continues.

Quota: Google's per-user limit is 6,000 units/min and `messages.get` costs 20. `QuotaGovernor` is a token bucket in quota units shared by all calls of one account; a 429 or `rateLimitExceeded` pauses *all* callers (honoring `Retry-After`), cuts the rate, and recovers it slowly. A per-day limit surfaces as `QuotaExhausted` ("try again later") rather than a retry loop.

## Verification

`Verifier` is read-only. It lists Gmail's message IDs and compares them with the archive (**complete**), re-hashes every `.eml` and extracted attachment against the size and SHA-256 recorded at download (**intact**), and runs SQLite's `quick_check`. The verdict is `Verified` only if Gmail was asked *and* nothing is missing or damaged; if Gmail couldn't be asked the verdict is `FilesIntactGmailNotChecked` and the UI says completeness isn't confirmed. A verification older than the last sync is shown as stale.

`ArchiveMaintenance.Repair` re-downloads damaged messages (by forgetting their rows and clearing the history position so the next sync is a full pass), but only for messages Gmail still has; for the rest it says so. `DeleteAccountData` is the only code that removes archived data, only on the user's typed confirmation, and refuses any path that is not an account folder inside the archive.

## Analytics

`AnalyticsMirror` copies changed rows from SQLite to DuckDB (Appender API) inside one DuckDB transaction per account. It opens the file for each operation and releases it, so external tools can open `analytics.duckdb` between updates; if another program has it open, the update is skipped and retried next time. A damaged file is renamed aside and rebuilt. `InsightsService` runs the canned queries behind the Insights tab. A rebuild produces exactly what incremental updates produced (tested).

## Desktop

- `ArchiveHost` owns the open archive (its folder is chosen in the app, so it may not exist at start-up).
- `SyncService` runs jobs in the background: one per account, two accounts downloading at once, graceful stop on quit. It raises `ActivityChanged`; `MainViewModel` coalesces these onto the UI thread and refreshes cards in place.
- `AccountPresenter` is a pure function from stored state and live activity to what a card says. All the wording about whether the archive is safe lives there and is unit-tested.
- Views are compiled-binding Avalonia XAML; the theme (`App.axaml`) and accent system are shared with Repo Watch. Motion runs only while something is working and is off with reduced motion.
- `AppShell` owns windows and dialogs behind the `IShell` interface, applies appearance, and shuts down cleanly.

Security-relevant choices: refresh tokens only in Windows Credential Manager (access tokens only in memory); HTTPS-only link opening with a host allow-list; message HTML is converted to text and never rendered; attachments open directly only for a short allow-list of inert types, everything else is *Show in folder*; logs and diagnostics are redacted; the update installer verifies a SHA-256 and only downloads from the repository's own release URLs.

## Testing

`dotnet test --solution GmailWatcher.slnx`. Archive tests cover ingest (hostile names, malformed mail, forwarded messages), the sync engine against the in-memory Gmail (first sync, history sync, history expiry, deletion safety, label reconciliation, stop and resume, failures), verification and repair, DuckDB equivalence and recovery, and search. Desktop tests cover presentation wording, view-model flows, services, settings recovery, the ported update installer, and headless renders that write screenshots to `artifacts/screenshots`. Google tests use scripted HTTP.
