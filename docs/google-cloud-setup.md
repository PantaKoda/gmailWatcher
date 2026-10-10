# Setting up Google sign-in

Gmail Watcher signs in to Google like any app that reads Gmail: it needs an **OAuth client**. Google makes every app have its own, so the one in this guide is **yours**, created once in your own (free) Google Cloud project. It takes about five minutes. Gmail Watcher uses it only on your PC, and it asks for **read-only** access.

You can do all of this from inside the app (*Set up Google…* opens a guided window with these same steps). This page is the detailed version.

## What you are creating, and why

| | |
| --- | --- |
| Project | A container in Google Cloud. Free; no billing needed for this. |
| Gmail API | Must be switched on for the project, or Google refuses every request. |
| Consent screen | What Google shows when you sign in. Lists the app name and the permission (`gmail.readonly`: *View your email messages and settings*). |
| OAuth client (Desktop app) | The Client ID and Client secret that identify Gmail Watcher to Google. For a Desktop app Google documents the secret as not confidential. |

## Steps

1. **Create a project.** Open <https://console.cloud.google.com/projectcreate>, name it (for example *Gmail Watcher*) and create it. Make sure it is the selected project in the top bar afterwards.
2. **Enable the Gmail API.** Open <https://console.cloud.google.com/apis/library/gmail.googleapis.com> and press **Enable**.
3. **Configure the consent screen.** Open <https://console.cloud.google.com/apis/credentials/consent> (Google may show it as *Google Auth Platform*).
   - User type: **External**.
   - App name: anything (*Gmail Watcher*), and your email as the support and developer contact.
   - **Scopes:** add `https://www.googleapis.com/auth/gmail.readonly`. It is listed as a *restricted* scope; that only matters for apps distributed to the public, not for your own use.
   - **Test users:** add every Gmail address you want to back up (you can also add them later).
4. **Publish the app (important).** On the Audience page choose **Publish app** so the status reads **In production**.
   - While the status is **Testing**, Google ends the sign-in after **7 days**: Gmail Watcher would show *Reconnect* every week.
   - You do **not** need to submit the app for verification. It is your own app. Google shows an *"unverified app"* warning when you sign in; choose **Advanced**, then **Go to … (unsafe)**. That is expected for an app you made yourself.
5. **Create the client.** Open <https://console.cloud.google.com/apis/credentials> → **Create credentials** → **OAuth client ID** → Application type **Desktop app** → Create.
6. **Give it to Gmail Watcher.** Either press **Import the downloaded JSON file** and pick the file from the *Download JSON* button in the confirmation, or paste the **Client ID** (ends in `.apps.googleusercontent.com`) and the **Client secret** into the window and press **Save**.

Then press **Add Gmail account**, sign in in the browser, and allow the read-only permission.

## Where the values go

Gmail Watcher writes them to `%LOCALAPPDATA%\GmailWatcher\gmailwatcher.config.json` (`Google:ClientId`, `Google:ClientSecret`). That file stays on your PC and is never part of the archive. You can also set the environment variables `GMAILWATCHER__Google__ClientId` and `GMAILWATCHER__Google__ClientSecret`.

## What Google lets this app do

Only the scope `gmail.readonly`. With it Gmail Watcher can read messages, labels and mailbox settings (filters, send-as aliases, forwarding). It cannot send, modify or delete anything. You can see and withdraw the access at any time at <https://myaccount.google.com/permissions>.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| *"This app isn't verified"* | Expected for your own app. **Advanced → Go to … (unsafe)**. |
| *"Access blocked: … has not completed the Google verification process"* or *"Error 403: access_denied"* | The Google account you chose isn't a **test user** and the app isn't published. Add the address under *Test users*, or publish the app (step 4). |
| *"The Gmail API has not been used in project … or it is disabled"* | Step 2 was skipped, or the client belongs to another project. Enable the Gmail API in the project the client was created in. |
| *Reconnect* appears about a week after setup | The app is still in **Testing** (step 4). Publish it, then reconnect once. |
| *Reconnect* still appears weekly although the app is **In production** | Google documents the 7-day limit only for the *Testing* status, but reports about unverified apps are mixed and this was **not verified against a real Google account when this app was built**. If it happens, nothing is lost: Reconnect takes seconds and the archive is untouched. Check that *Publishing status* really says *In production*. |
| *Reconnect* appears after a long time or after a password change | Google ends sign-ins that were unused for six months, and, for Gmail, when the account password changes. Press **Reconnect**: the archive is untouched. |
| *"redirect_uri_mismatch"* | The client isn't a **Desktop app** type. Create a new client of that type. |
| You unticked the permission on Google's screen | Google lets people untick *View your email messages and settings*. Gmail Watcher notices and asks you to try again with it ticked. |

## Google Workspace (work or school) accounts

An administrator can restrict which apps may access Gmail. If sign-in says the app is blocked by your organisation, the administrator has to allow the client (or you use the client from a project in your organisation's own Google Cloud, marked *Internal*, which needs no publishing or test users).
