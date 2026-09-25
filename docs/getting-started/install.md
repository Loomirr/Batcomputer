# Install Batcomputer

## Fresh installation

1. Open [GitHub Releases](https://github.com/Loomirr/Batcomputer/releases) and choose the latest stable release.
2. Download the Windows x64 ZIP, not GitHub's **Source code** archive. The accompanying `.sha256` is a checksum, not a second installer.
3. Extract the **entire** ZIP to a writable folder such as `C:\Tools\Batcomputer`. Do not run it inside the archive.
4. Start the top-level **Batcomputer.exe** and complete [first-time setup](setup.md).

Keep the whole folder together. Batcomputer includes its .NET runtime; moving only the EXE will not work.
Avoid Program Files, the game directory and cloud-synchronized folders for your authoring workspace.

## Expected portable layout

```text
Batcomputer/
  Batcomputer.exe
  app/                 application DLLs and bundled .NET runtime
  Tools/               packaging, registry and Blender helpers
  gamedata/            catalogs and metadata
  Documentation/       bundled documentation
  licenses/            dependency licenses and notices
```

Settings and workspace folders are created as you use the app. They are not supplied as empty
replacements in an update ZIP. See [Workspace and files](../reference/workspace.md).

## Updating an existing installation

If your version has **Updates**, use the [in-app updater](../guides/app-updates.md).
Older builds without a compatible updater need one manual installation.

To update in place:

1. Close every Batcomputer window and back up your settings, projects and source assets.
2. Extract the complete new ZIP into the **same Batcomputer folder**, allowing application files to be replaced.
3. Do not delete the old folder first. Keep **Batcomputer.settings.json** and your existing workspace data.
4. Launch the top-level EXE, check the displayed version, and open an existing project.
5. Check Settings paths and run **Check mod** before rebuilding.

This carries over local projects and settings when you merge the archive correctly. Projects stored
elsewhere stay in their existing locations. A manual extraction does not create an updater rollback;
your backup is the recovery copy. Files left by older layouts may remain—do not delete unfamiliar
folders while trying to tidy the installation.

Prefer a separate new folder? Follow [Back up and move a workspace](../guides/backups-and-moving.md)
so copied settings do not accidentally keep pointing at the old installation.

## Check a download's SHA-256

The 1.0 release has two ZIP names:

| File | Purpose |
| --- | --- |
| `Batcomputer-v1.0.0-win-x64.zip` | Versioned download for manual installation |
| `Batcomputer-update-win-x64.zip` | The fixed filename used by the built-in updater |
| The matching `.zip.sha256` | A small text checksum for that ZIP, not an installer |

For **1.0.0**, both ZIPs contain exactly the same files and have the same SHA-256. Download only
the versioned ZIP for a manual install; you do not need both. The two checksum files name their
respective ZIPs, so the checksum files themselves need not have matching hashes.

In PowerShell, run this against the ZIP you downloaded:

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath 'C:\Downloads\Batcomputer-v1.0.0-win-x64.zip'
```

Compare the full hash with the matching checksum from the same release. A mismatch means you
should download again before extracting. Matching hashes check file integrity; use the official
repository as the download source.

## What players need

Install a compatible **Loomirr's LOTDK UE4SS** framework before installing your generated mod.
Players need the framework and the finished mod, not Batcomputer or Unreal Engine.
See [Requirements](requirements.md), especially for Mayhem mode support.
