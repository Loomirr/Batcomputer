# Included tools

Batcomputer keeps its helper tools in this folder so a portable release can be unpacked and set up in one place.

- `retoc-oodle\retoc.exe` packs mod releases and extracts game assets. It is built from the MIT-licensed `retoc-oodle` fork. The proprietary Oodle runtime is never included; point Setup at your local UE 5.6 copy when enabling compressed builds.
- `Build-NativeSuitTemplateIndex.ps1` builds the character/template index after the first full extraction.
- `BatcomputerRegistryWriter` contains Batcomputer's small UE 5.6 Asset Registry writer. The portable includes a verified module for Epic's current UE 5.6 build and keeps the source as a fallback for a different engine `BuildId`. Batcomputer copies the project to a short per-user cache so Unreal's generated paths stay within its Windows limit.

These helpers contain no game files or extracted game assets.

## Working from source

The source repository also contains contributor utilities that are not all copied into the
portable release: `Animations/`, `Icons/`, `SkinnedMesh/` and `Blender/` support authoring;
`Documentation/` builds/checks the guide site; `Updater/` prepares and validates release packages.
Development `.ps1` files are not extra game mods and should not be copied into a game installation.

To build the documentation from the repository root:

```powershell
python -m pip install -r Tools/Documentation/requirements.txt
python -m mkdocs build --strict
python Tools/Documentation/check_site.py site
```

`Publish-GitHubPrerelease.ps1` defaults to beta publication. The explicit `-Stable` switch accepts
only a stable semantic version and marks that release latest. Always validate with `-DryRun`
first, using packages from the exact tested, pushed source revision. See the updater maintainer
notes for packaging and disposable-install tests.
