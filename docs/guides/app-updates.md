# Updating Batcomputer

Your version appears under the Batcomputer logo, in the window title and in Settings.
Click the version, choose **Updates** from the main menu, or open it from Settings.

## Download and restart

1. Open the Update center and expand **Settings & recovery** if you want to change the release channel.
2. Choose **Check for updates**. Read **What's new** and check the download size.
3. Choose **Download** and wait for verification to finish.
4. Save your work, choose **Restart now**, and confirm. Batcomputer closes normally and reopens after installation.
5. The **Update complete** prompt shows the version now installed. It appears once, not on every launch.

You can use **Install when I exit** instead. Close the whole app, including any other instance
running from the same folder, within ten minutes. Closing just the Update center does not install it.
If an unsaved-work prompt cancels the restart, save or finish that work and try again within the
scheduled window. Batcomputer does not force-close your editors.

## Stable and beta releases

- Leave beta releases disabled for stable updates.
- Enable betas to see eligible prereleases as well as stable releases. This does not downgrade a newer installed version.
- Optional startup checks notify you; they do not download or install automatically.
- Versions before 1.0 are legacy and are never offered by the updater. 1.0 betas remain eligible with betas enabled.

Your saved channel survives updates and manual installations. Installing a beta over a Stable
installation does **not** change that saved preference. If an announced beta is missing, open
**Settings & recovery**, select **Beta**, then **Check for updates**. For example, 1.0.0 is still
the latest stable release while 1.1.0-beta.3 requires the Beta channel. Older Update centers can
say “You're all set” when they mean only that the selected channel has no newer update.

The revised Update center names the checked channel, explains when a newer beta is excluded,
and checks again immediately when you switch channels. It never enables betas automatically.

If an older version has no Updates menu or cannot read the new package layout, use the
[manual update instructions](../getting-started/install.md#updating-an-existing-installation) once.

## What happens to projects and mods?

The updater replaces application files, not your projects, settings, `Data`, `Generated`, local
`Runtime` state, game files or installed mods. Your workspace stays where it is. Still keep normal
project backups: an application rollback is not a backup of your creative work.

Keep `app/`, `Tools/` and the other release folders beside the launcher. The organized layout is
intentional; its DLLs and runtime are needed even though there is only one top-level EXE.

## Smaller downloads

The updater can reuse unchanged files **when a release provides a changed-file catalog**. It checks
local file hashes, downloads changed or missing files, then verifies the complete staged app.
A changed DLL is downloaded in full, compressed; this is not a binary-delta patch.

Both **1.0.0 stable and 1.1.0-beta.1 use the full ZIP**. The beta includes the new patch-ZIP
reader for future releases, but its own download is full-sized. Do not expect every update
to be a small download. New runtimes, changed dependencies or missing local files can increase its size.
The Update center shows the download needed for the selected release.

Starting with the 1.1 beta updater, a release can instead offer a **base-version patch ZIP**.
It is selected only when your installed version matches the patch's starting version and every
file it needs to reuse matches the target hash. A different starting version, or a missing/modified
reused dependency, uses the full ZIP. Files are checked again while staging; a change after checking
can trigger the full fallback. Corrupt downloads are rejected rather than installed.

Original 1.0 updater builds do not read patch-ZIP catalogs. They download the full ZIP to gain
this feature first. The full ZIP remains available for new users and manual installations.

The public **Batcomputer-update-win-x64.zip** is also the complete fresh-install download. There
is no separate app ZIP to install first; the fixed name is how existing updaters find it.

## Recovery

Expand **Settings & recovery** and choose **Backups / recovery**. Backups live in
`.batcomputer-updates` beside the launcher, separated by update transaction.

If the new app exits before its main window opens, the update helper attempts to restore the
previous application files. A slow or blocked app is not forcibly killed. The completion prompt
means installation and startup succeeded, not that every mod or workflow has been tested.

To restore application files manually:

1. Close Batcomputer and wait for any update helper to finish.
2. Open the transaction for the update you want to undo. Read `status.txt` to identify it.
3. Open its `helper` folder and run that private `Batcomputer.exe`.
4. Follow the restore prompt. Recovery refuses to overwrite application files changed since that update.
5. Reopen the normal top-level launcher and check its version.

Settings and projects are not rolled back. Keep the entire transaction folder until you are happy
with the update; it contains the backup and recovery journal. Do not delete it while installation
or recovery is pending. Older finished transactions can be removed manually after all helpers exit.

## If an update fails

Older updaters can fail to start their installation helper from deeply nested folders, even
when the main app opens normally. If a verified download never installs after exit, check the
transaction's `status.txt`. Close Batcomputer, move the **complete portable folder** to a shorter
path (for example `C:\Tools\Batcomputer`), then check/download again. Do not move only the EXE
or run a helper from a different transaction. The revised updater confirms helper startup and
uses extended-length launch paths; older installed clients cannot gain that fix before updating.

Check **Technical details** and copy the useful error text. For a checksum/verification error,
retry the download rather than extracting the rejected payload yourself. For a file-lock error,
close other Batcomputer instances and anything inspecting its application files, then retry.

If necessary, use the official full ZIP and your backup to perform a manual update. Include your
old/new version and transaction status when [reporting a problem](../help/reporting-issues.md).
