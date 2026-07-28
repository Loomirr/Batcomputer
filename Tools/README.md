# Portable Author Tools

Batcomputer keeps its author-side helper tools in this folder so a release can be unpacked and configured in one place.

- `retoc-oodle\retoc.exe` packs mod releases and extracts game assets. It is built from the MIT-licensed `retoc-oodle` fork. The proprietary Oodle runtime is never included; point Setup at your local UE 5.6 copy when enabling compressed builds.
- `Build-NativeSuitTemplateIndex.ps1` builds the character/template index after the first full extraction.
- `SuitSlotsRegistryWriter` contains only the UE 5.6 project source and configuration for the static Asset Registry writer. Unreal creates its own `Binaries`, `Intermediate`, `Saved`, and cache folders here when an author first builds a registry.

These helpers contain no game files or extracted game assets.
