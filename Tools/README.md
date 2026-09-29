# Included tools

Batcomputer keeps its helper tools in this folder so a portable release can be unpacked and set up in one place.

- `retoc-oodle\retoc.exe` packs mod releases and extracts game assets. It is built from the MIT-licensed `retoc-oodle` fork. The proprietary Oodle runtime is never included; point Setup at your local UE 5.6 copy when enabling compressed builds.
- `Build-NativeSuitTemplateIndex.ps1` builds the character/template index after the first full extraction.
- `BatcomputerRegistryWriter` contains Batcomputer's small UE 5.6 Asset Registry writer. The portable includes a verified module for Epic's current UE 5.6 build and keeps the source as a fallback for a different engine `BuildId`. Batcomputer copies the project to a short per-user cache so Unreal's generated paths stay within its Windows limit.

These helpers contain no game files or extracted game assets.
