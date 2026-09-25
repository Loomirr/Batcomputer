# Batcomputer update view

The included GLB is derived from the user-supplied `Batcomputer3D.fbx` and adjacent `BatcomputerTex.png`. It preserves all four original meshes. `LeverHandle` is attached to `LeverPivot` at its hinge and animated by the exported `LeverCycle` clip. The scale is uniformly normalized for presentation, not a change to any imported game asset workflow.

Run `Tools/Updater/Prepare-BatcomputerModel.py` with Blender in background mode and `-- source.fbx output-directory` to regenerate the GLB, still-image fallback, and editable Blender source. The FBX and original texture are never overwritten. Copy the generated GLB/poster here; the editable `.blend` is not shipped in the app.

The material shader lights only strongly colored regions of the existing control-panel texture; it does not replace the texture or illuminate the whole console. Rendering is capped at 30fps and 1.5 device pixel ratio. Page visibility and reduced-motion preference suspend animation. No internet model/CDN resources are used.

The page is presentation-only. Native C# code still checks/downloads/verifies packages, asks for install approval and starts the helper. Only allowlisted commands from this instance's local virtual host are accepted. Release notes and logs are displayed as text, never interpreted as HTML. A standalone browser exposes labeled simulated states for design testing only.
