# Third-party notices

Batcomputer's portable release includes **THIRD_PARTY_NOTICES.md** and a **licenses** folder.
Keep both with the application. The notices identify the bundled dependencies; this page is a
guide to those files, not a replacement for their licenses.

## Packaging helper

`Tools/retoc-oodle/retoc.exe` is built from the
[retoc-oodle fork](https://github.com/Loomirr/retoc-oodle) of
[retoc](https://github.com/trumank/retoc). Its MIT license is included as
`licenses/retoc-oodle-MIT.txt`. Native build provenance is recorded under `licenses/native`.

The helper uses a configured local Oodle runtime when needed. Batcomputer does not bundle or
download `oo2core_9_win64.dll`; use your own compatible local installation.

## Preview and application libraries

The CUE4Parse native helper has its own notice plus upstream CUE4Parse, ACL and RTM notices under
`licenses/native`. three.js and its export helpers have separate license files. Other bundled
application dependencies have notices under `licenses/dependencies`.

The `app` folder contains the self-contained runtime and application dependencies. These are
required application files, not optional clutter.

## Registry writer and external requirements

Batcomputer includes its authored registry-writer helper and source fallback. It still needs a
local compatible Unreal Engine 5.6 installation. The application does not include Epic's editor
runtime, the game, extracted game content, mappings or the game-side UE4SS framework.

Do not add those local runtimes or your extracted Content folder to a Batcomputer redistribution.
The tool's license and dependency notices do not grant blanket rights to redistribute game assets
or third-party art. Keep player mod releases separate from your authoring installation.

For the full packaged notice, see
[THIRD_PARTY_NOTICES.md](https://github.com/Loomirr/Batcomputer/blob/main/THIRD_PARTY_NOTICES.md).
