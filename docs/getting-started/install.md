# Install Batcomputer

## Portable installation

1. Download the current `Batcomputer-...-win-x64.zip` release.
2. Extract the **entire** archive to a writable folder such as `C:\Tools\Batcomputer`.
3. Do not run the executable from inside the ZIP.
4. Start `Batcomputer.exe`.

Install Loomirr's LOTDK UE4SS 0.1.1 or newer in the game before using Batcomputer's direct mod
installation. That framework release supplies the shared registry configuration used by every
Batcomputer-authored suit mod.

Batcomputer stores its settings, indexes, projects, and generated files beside the application
unless you choose another workspace. Avoid `Program Files`, the game directory, and
cloud-synchronized folders for the portable itself when possible.

## Expected portable layout

```text
Batcomputer/
  Batcomputer.exe
  CUE4Parse-Natives.dll
  gamedata/
  Generated/
  Documentation/
  licenses/
  Tools/
```

If a required bundled file is missing, Batcomputer reports an incomplete portable install instead
of continuing with a half-working package.

## Updating

1. Close Batcomputer.
2. Keep a backup of your existing folder.
3. Extract the new portable over a new folder.
4. Copy `Batcomputer.settings.json`, `Generated`, and `Data\Mappings` into the new folder if you used
   the default portable workspace. Let Batcomputer rebuild `Data\Cache` and other indexes.
5. Launch the new version and open one existing project before deleting the old portable.
6. Run **Check mod** before rebuilding or installing that project.

During the beta, keep each portable version in its own folder until the new version has opened your
projects successfully.

If the game dump also changed, follow [Update or repair a suit](../guides/update-repair-suit.md)
before rebuilding.

Next: [First-time setup](setup.md).
