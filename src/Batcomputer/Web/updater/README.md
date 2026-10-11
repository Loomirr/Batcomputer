# Update center interface

This folder contains the offline web interface embedded in Batcomputer's desktop Update center.
It is application source, not a separate updater to download or install.

For update instructions, see [Updating Batcomputer](../../../../docs/guides/app-updates.md).
For the complete application, use [GitHub Releases](https://github.com/Loomirr/Batcomputer/releases).

## Files

- `index.html`, `updater.css`, `updater.js`: layout, styling and desktop-interface messages.
- `Batcomputer.glb`: the animated console model.
- `Batcomputer-poster.png`: the still-image fallback when 3D rendering is unavailable.

The model and scripts are bundled locally; no model CDN is required. Animation pauses when hidden
or when reduced motion is requested. The view is decorative: it does not modify game assets.

## Contributor notes

Native C# services perform release discovery, package verification, user confirmation and installation.
The web view accepts only allowlisted messages from its local host. Notes and logs are rendered as text.
Opening this page in a standalone browser uses labeled simulated states, not real update operations.

`Tools/Updater/Prepare-BatcomputerModel.py` regenerates the model, fallback image and an editable
Blender scene from a compatible source model. Run Blender in background mode with
`-- source.fbx output-directory` after the script arguments. The expected model has `Batcomputer`,
`BatSymbol`, `LeverBase` and `LeverHandle` meshes; its handle uses the `LeverCycle` animation.
The original FBX/texture and editable Blender scene are not required at runtime.

For packaging and isolated update tests, see the [maintainer notes](../../../../docs/research/updater-maintainer-notes.md).
