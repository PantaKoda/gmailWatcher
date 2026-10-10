# Releases and in-app updates

## For maintainers: publishing a release

1. Set `<Version>` in `Directory.Build.props` (e.g. `0.3.0`).
2. Add a `## [0.3.0] - YYYY-MM-DD` section to `CHANGELOG.md`, written for people using the app. It becomes the release notes and is what the update window shows.
3. Merge to `main`, then push a tag for that version:

   ```bash
   git tag v0.3.0
   ```

   ```bash
   git push origin v0.3.0
   ```

4. The **Release** workflow (`.github/workflows/release.yml`) checks that the tag matches the version, takes the notes from `CHANGELOG.md`, runs `scripts/publish-windows.ps1` (tests included), and creates the GitHub release with `GmailWatcher-<version>-win-x64.zip` and its `.sha256`.

Requirements: the repository must be **public**, so the app can read releases without signing in and anyone can download them. Pre-release and draft releases are ignored by the app. Only `vX.Y.Z` tags count.

## For users: how updates work

- **Checking:** once a day (and at startup after 30 seconds), Gmail Watcher asks GitHub for the releases of `Updates:Repository` (default `PantaKoda/gmailWatcher`), anonymously. *Check for updates* in Settings asks right away and either says there is no newer release or opens the update window.
- **Indicator:** when a newer release exists, an **UPDATE** button appears in the window header. It opens the update window with the notes of every release since your version, newest first.
- **Install update** (only when you click it):
  1. Downloads the release's `.sha256` file and its zip, only from that repository's release download URLs on github.com, over HTTPS.
  2. Checks the zip's SHA-256 against the published checksum. On a mismatch nothing is installed.
  3. Unpacks it into the data folder (`%LOCALAPPDATA%\GmailWatcher\updates`) and checks that it contains Gmail Watcher at that version.
  4. Starts the new copy as the updater and quits. The updater waits for Gmail Watcher to exit, moves the install folder aside as `<folder>.previous`, copies the new version in, and starts it. The new version says "Updated to X" and tidies the staging folder.
  5. If the copy fails, the previous folder is put back and started again, and it tells the user the update couldn't be applied (the reason is in `logs\update.log`).
- **Cancel and stalls:** the download can be cancelled from the update window, and one that receives nothing for 30 seconds gives up. Either way nothing is changed and installing can be tried again.
- **Layout requirement:** the data folder must not be inside the app folder (for example, don't extract the zip directly into `%LOCALAPPDATA%`). In that layout the app explains that it can't update itself.
- **What is kept:** Settings, sign-in and above all **your mail archive** live outside the app folder and are never touched by an update. Nothing else needs to be moved.
- **Rolling back:** quit Gmail Watcher, delete the install folder and rename `<folder>.previous` back. The previous version stays until the next update.
- **Copies built from source** never replace themselves; the window offers the GitHub page instead. Only a folder extracted from a release zip has the `release.json` marker that allows it.

## Security notes

- The checksum proves the download is the file published with the release, and HTTPS from github.com proves it came from GitHub. Neither is a **code signature**: someone able to publish a release on the repository could publish a malicious one. Signing the executable (Authenticode) and signed update metadata remain future work.
- Release notes are untrusted text: they are shown as plain text, never rendered as markup.
- Update checks never send the user's GitHub token.
- `Updates:CheckIntervalHours` (default 24; 0 = only when asked) keeps well within GitHub's 60 anonymous requests per hour.
