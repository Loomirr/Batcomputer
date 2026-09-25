# Update center: in-app acceptance

## Prepared disposable copy

**Latest smaller-download fixture:** `artifacts/updater-smaller-test/installed/Batcomputer.exe`, full app `.1` → `.2`, feed port **8761**. Both versions have the actual model and folder-based publishing. Expected: 6,450,055 download bytes, 369 reused files (full ZIP: 81,498,015 bytes). See that fixture's `TEST-INSTRUCTIONS.md`. Its `installed` copy is left at `.1` for manual testing; automated acceptance used a separate copy.

**Latest actual-model fixture:** `artifacts/updater-actual-model/installed/Batcomputer.exe`, full app `.1` → `.2`, feed port **8758**. Both versions now show the original FBX-derived Batcomputer, articulated lever, and illuminated panel. The older fixture below retains the earlier layout. For this latest test also check Pause animation, expandable notes/settings/details, and that verification shows an indeterminate bar rather than a fabricated percentage. The primary verified action is now **Install when I exit**.

Launch `artifacts/updater-full-app-polish/installed/Batcomputer.exe` from this repository. Its title must include **LOCAL TEST** and version **99.0.0-updatertest.1**. The normal workshop opens with an empty disposable workspace. Do not point it at real projects or game folders for this test.

The local feed is `http://127.0.0.1:8756/releases.json`. Keep its test server running; the specific process ID is in `artifacts/updater-full-app-polish/server-pid.txt`. No GitHub release is involved.

## Main flow

1. Click the version under the logo. Confirm the redesigned Update center opens. Resize to minimum size: no clipped buttons, overlapping text or unexpected horizontal scrolling.
2. Choose **Stable**, then **Check for updates**. The beta-only fixture must not be offered. Switch to **Beta** and check again: `.2` should appear with notes and download size.
3. Start **Download**, then **Cancel** while progress is moving. The app must remain responsive, no install action should be available, and retry should work.
4. Download again and wait for verification. It should say it is ready and enable **Install on exit**. Close only the Update center; reopen it from the main menu's **Updates** entry. The verified package should still be ready without another download.
5. Optionally change the disposable app's theme or a harmless preference and save it. Do not create/install a real game mod.
6. Choose **Install on exit** and accept its confirmation. Closing just the Update center must leave the workshop running. Close the **whole disposable app** to permit installation.
7. The full workshop should restart as **99.0.0-updatertest.2 · LOCAL TEST**, not as the old standalone update dialog. Open Updates: live activity should report startup confirmed, refreshing automatically if the helper is still finishing.
8. Check again: there should be no newer version. Confirm the theme/preference survived and `Generated/KEEP-ME.txt` still exists in this disposable installation.

## Optional failure and recovery checks

- Offline: stop only the server named by this fixture's `server-pid.txt`; Check should show a readable error without freezing or closing the workshop. Run a new fixture for further downloads.
- Bad checksum: run `./Tools/Updater/Start-LocalUpdateTest.ps1 -FullApp -Scenario Corrupt -Port 8757` with a fresh output root. Download must fail verification, with no install action and no change to the running version.
- Recovery: after a successful update, open **Backups / recovery**, close the entire test app, then run that transaction's `helper/Batcomputer.exe`. Accept recovery for this disposable copy only; reopening the installed app should show `.1`, with settings and KEEP-ME preserved.

Report the step, version, screenshot and Live activity text for any failure. Startup confirmation alone is not a complete workshop feature test.
