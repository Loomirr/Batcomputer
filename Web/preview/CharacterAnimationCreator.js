// Local animation-draft editor. This never changes a saved suit or writes cooked game assets.
window.BatcomputerCharacterAnimationCreator = function ({ THREE, scene, camera, renderer, controls, loaded, root, preview }) {
  const panel = document.createElement('section'); panel.id = 'cw-animation-creator';
  const body = loaded.find(entry => entry.m.part === 'CharacterMesh0');
  const bones = new Map();
  body?.scene.traverse(node => { if (node.isBone && !bones.has(node.name)) bones.set(node.name, node); });
  if (!bones.size) {
    panel.textContent = 'This character has no body rig available for animation authoring.';
    return { panel, enter() {}, leave() {}, update() {}, withRestPose() { return null; } };
  }
  const rest = new Map([...bones].map(([name, bone]) => [name, {
    p:bone.position.clone(), q:bone.quaternion.clone(), s:bone.scale.clone()
  }]));
  const rigSignature = [...bones].map(([name, bone]) => `${name}:${bone.parent?.isBone ? bone.parent.name : ''}`).join('|');
  const tracks = new Map();
  const fps = 30;
  let durationFrames = 60, frame = 0, selectedBone = bones.has('Chest') ? 'Chest' : [...bones.keys()][0], active = false, playing = false, last = 0, loop = true, dragging = false;
  let selectedBones = new Set([selectedBone]), boneGroups = [], selectedGroup = '';
  const collapsedGroups = new Set();
  let clipName = 'New animation', clipDescription = '', playbackRate = 1, zoom = 1, dirty = false, reelDirty = true;
  let copiedKey = null, lastReelFrame = -1, lastUiFrame = -1, keysDirty = true, keyCount = 0;
  const markersByFrame = new Map(), selectedKeysByFrame = new Map();
  const playheads = [];
  let combatWindows = [], selectedWindow = '', sourceAnimationPackage = '';
  let voiceCues = [], selectedVoice = '';
  const voiceTags = { VOX_Combat:'Attack · normal effort', VOX_CombatSml:'Attack · small effort', VOX_CombatLrg:'Attack · heavy effort', VOX_Jump:'Jump effort' };
  let libraryDraftId = '', libraryEntries = window.PREVIEW_USER_ANIMATIONS || [];
  let revision = 0, draftGeneration = 0, pendingLibrarySave = null;
  const undoStack = [], redoStack = [];
  const dock = document.createElement('div'); dock.className = 'cw-creator-dock'; dock.hidden = true;
  dock.innerHTML = '<div class="cw-creator-resize" role="separator" tabindex="0" aria-label="Resize animation timeline" aria-orientation="horizontal" aria-valuemin="160" title="Drag up or down to resize the animation timeline"><span></span></div>' +
    '<div class="cw-creator-transport"><strong>ANIMATION TIMELINE</strong>' +
    '<button type="button" class="first" title="First frame">⏮</button><button type="button" class="previous" title="Previous frame">◀</button>' +
    '<button type="button" class="dock-play" title="Play or pause">▶ Play</button><button type="button" class="next" title="Next frame">▶</button>' +
    '<button type="button" class="last" title="Last frame">⏭</button><label><input type="checkbox" class="loop" checked> Loop</label>' +
    '<label>Speed <select class="speed" aria-label="Playback speed"><option value="0.25">0.25×</option><option value="0.5">0.5×</option><option value="1" selected>1×</option><option value="2">2×</option></select></label>' +
    '<label>Frame <input type="number" class="frame-number" aria-label="Current animation frame" min="0" max="60" step="1" value="0"></label>' +
    '<span class="dock-time"></span></div>' +
    '<input type="range" class="dock-timeline" aria-label="Animation playback timeline" min="0" max="60" value="0" step="1">' +
    '<div class="cw-creator-timeline-tools"><span>Drag diamonds to retime keys · click a track to scrub</span><label>Zoom <select class="zoom" aria-label="Timeline zoom"><option value="1">Fit</option><option value="2">2×</option><option value="4">4×</option><option value="8">8×</option></select></label></div>' +
    '<div class="cw-creator-scroll"><div class="cw-creator-ruler"></div><div class="cw-creator-reel" aria-label="Animation keyframe tracks"></div></div>';
  panel.innerHTML = '<div class="cw-creator-heading"><strong>Animation studio</strong><span class="draft-indicator">Draft</span></div>' +
    '<small>Animate the native body rig. Save a draft to keep your work; a draft is not a cooked game animation.</small>' +
    '<div class="cw-creator-section">YOUR ANIMATIONS</div>' +
    '<label>Show<select class="library-filter" aria-label="Your animation filter"><option value="all">All your drafts</option><option value="used">On this character</option><option value="ready">Cooked drafts</option></select></label>' +
    '<input type="search" class="library-search" aria-label="Search your animations" placeholder="Search your animations or assigned slots…">' +
    '<label>Draft<select class="library-picker" aria-label="Your animations"></select></label>' +
    '<small class="library-info"></small><div class="cw-creator-buttons"><button class="library-open" type="button">Preview / edit</button><button class="library-save" type="button">Save to your animations</button></div>' +
    '<small>Private to this workspace. “On this character” shows saved slot assignments, not the donor’s default animations. Build to apply changes in-game.</small>' +
    '<div class="cw-creator-section">CLIP</div>' +
    '<label>Name<input class="clip-name" aria-label="Animation clip name" maxlength="64" value="New animation"></label>' +
    '<label>Purpose<input class="clip-description" aria-label="Animation clip description" maxlength="160" placeholder="e.g. relaxed idle, wave, victory…"></label>' +
    '<label>Length · seconds<input type="number" class="length" aria-label="Animation length" min="0.1" max="30" step="0.1" value="2"></label>' +
    '<small>30 frames per second · 0.1–30 seconds. The timeline below the character controls playback.</small>' +
    '<div class="cw-creator-buttons"><button type="button" class="from-motion">Edit selected Motion clip</button></div>' +
    '<small>Select a loaded body animation in Motion first. This copies its poses into editable keys; it does not copy gameplay notifies.</small>' +
    '<div class="cw-creator-section">START FROM EXISTING MOTION</div>' +
    '<input type="search" class="source-search" aria-label="Search source animations" placeholder="Find a native or your cooked animation…">' +
    '<label>Source clip<select class="source-picker" aria-label="Source animation"></select></label>' +
    '<div class="cw-creator-buttons"><button type="button" class="source-edit">Copy to editable draft</button><button type="button" class="source-refresh">Refresh your cooked clips</button></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="source-pak">Import cooked package…</button><button type="button" class="source-blender">Import Blender action…</button></div>' +
    '<small>Import a package into your private library, or choose a saved Blender action already on this exact LOTDK body rig. Source files are unchanged. Gameplay notifies are not copied; save as a new draft to make a variation.</small>' +
    '<div class="cw-creator-section">RIG · SELECT JOINTS</div>' +
    '<small>Click a bone; Ctrl-click adds/removes it, Shift-click selects a range. Transforms and key actions affect all selected bones.</small>' +
    '<small class="selection-info" role="status"></small>' +
    '<label>Find bone<input type="search" aria-label="Find animation bone" placeholder="Search bones…"></label>' +
    '<label>Bone<select aria-label="Animation bone"></select></label>' +
    '<div class="cw-creator-bone-list" aria-label="Skeleton bone hierarchy"></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="focus-joint">Focus joint</button><button type="button" class="toggle-joints" aria-pressed="false">Show joints</button></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="select-all-bones">Select all</button><button type="button" class="select-one-bone">Only primary bone</button></div>' +
    '<div class="cw-creator-section">BONE GROUPS</div>' +
    '<small>Organize tracks into named divisions, such as Breathing or Arms. Groups are not motion layers; each bone has one track.</small>' +
    '<label>Group<select class="bone-group" aria-label="Bone group"></select></label>' +
    '<label>Name<input class="group-name" aria-label="Bone group name" maxlength="48" placeholder="e.g. Breathing"></label>' +
    '<div class="cw-creator-buttons"><button type="button" class="group-create">Create from selection</button><button type="button" class="group-assign">Assign selection</button></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="group-select">Select group</button><button type="button" class="group-rename">Rename</button><button type="button" class="group-delete">Delete group</button></div>' +
    '<div class="cw-creator-section">TRANSFORM · SELECTED BONE</div>' +
    '<div class="cw-creator-buttons cw-creator-tools"><button type="button" class="move active">Move · W</button><button type="button" class="rotate">Rotate · R</button><button type="button" class="scale">Scale · E</button></div>' +
    '<label>Axes <select class="axes" aria-label="Transform axes"><option value="local">Local to bone</option><option value="world">World</option></select></label>' +
    '<label>Snap <span class="cw-creator-inline"><input type="checkbox" class="snap" aria-label="Snap transforms"><input type="number" class="snap-degrees" aria-label="Rotation snap degrees" min="1" max="90" value="15">°</span></label>' +
    '<small>Numeric edits apply the primary bone’s change to the selection, preserving pose differences. 3D handles transform selected roots together (parents are not applied twice). Position is in viewer units; scale is a rest-pose multiplier.</small>' +
    '<div class="cw-creator-grid"><span>Rotation offset · degrees</span><label>X<input type="number" data-kind="rotation" data-axis="0" step="1"></label>' +
    '<label>Y<input type="number" data-kind="rotation" data-axis="1" step="1"></label>' +
    '<label>Z<input type="number" data-kind="rotation" data-axis="2" step="1"></label></div>' +
    '<div class="cw-creator-grid"><span>Position offset · viewer units</span><label>X<input type="number" data-kind="position" data-axis="0" step="0.001"></label>' +
    '<label>Y<input type="number" data-kind="position" data-axis="1" step="0.001"></label>' +
    '<label>Z<input type="number" data-kind="position" data-axis="2" step="0.001"></label></div>' +
    '<div class="cw-creator-grid"><span>Scale · rest-pose multiplier</span><label>X<input type="number" data-kind="scale" data-axis="0" min="0.05" max="5" step="0.01"></label>' +
    '<label>Y<input type="number" data-kind="scale" data-axis="1" min="0.05" max="5" step="0.01"></label>' +
    '<label>Z<input type="number" data-kind="scale" data-axis="2" min="0.05" max="5" step="0.01"></label></div>' +
    '<input type="range" class="timeline" aria-label="Animation creator timeline" min="0" max="60" value="0" step="1">' +
    '<small class="time"></small><div class="cw-creator-section">KEYFRAMES</div>' +
    '<label>Transition <select class="interpolation" aria-label="Selected key interpolation"><option value="linear">Linear</option><option value="smooth">Smooth</option><option value="hold">Hold</option></select></label>' +
    '<small>Transition controls how this key moves into the next one. Hold keeps the pose until the next key.</small>' +
    '<div class="cw-creator-buttons"><button type="button" class="play">Play</button><button type="button" class="key">Set key</button><button type="button" class="remove">Delete key</button></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="copy-key">Copy key</button><button type="button" class="paste-key">Paste key</button><button type="button" class="reset-bone">Key rest pose</button></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="clear-track">Clear bone track</button><button type="button" class="undo">Undo</button><button type="button" class="redo">Redo</button></div>' +
    '<small>Keys for selected bone</small><div class="keys"></div>' +
    '<small>Shortcuts: W move · R rotate · E scale · K key · Space play · ←/→ one frame · Shift+←/→ five frames · Ctrl+Z/Y undo/redo · Ctrl+S save.</small>' +
    '<div class="cw-creator-section">VOICE CUES · COOKED WITH ANIMATION</div>' +
    '<small>Request the playing character’s voice at a frame. Recordings come from Character voices, not from this draft. No audio playback here; test in-game. Add the cue to the clip OR its montage, not both.</small>' +
    '<label>Cue<select class="voice-picker" aria-label="Voice cue"></select></label>' +
    '<div class="cw-creator-buttons"><button type="button" class="voice-add">Add voice here</button><button type="button" class="voice-remove">Remove voice</button></div>' +
    '<label>Category<select class="voice-tag" aria-label="Voice cue category">' + Object.entries(voiceTags).map(([tag,label])=>`<option value="${tag}">${label}</option>`).join('') + '</select></label>' +
    '<label>Frame<input type="number" class="voice-frame" aria-label="Voice cue frame" min="0" step="1"><button type="button" class="voice-playhead">Use playhead</button></label>' +
    '<label><input type="checkbox" class="voice-empty" aria-label="Play voice on empty swings"> Play on empty swings</label>' +
    '<small>Allows attack effort cues through animation request filtering, including swings without a target. Dialogue cooldowns and interruptions still apply.</small>' +
    '<small>Use one effort cue near each swing. Save the draft, cook it, assign the cooked clip, then build your mod. Hit windows below remain separate preview markers.</small>' +
    '<div class="cw-creator-section">COMBAT TIMING · PREVIEW DRAFT</div>' +
    '<small>Plan active hit windows against the pose. These markers are not game damage events; native notify integration is still required before cooking combat timing.</small>' +
    '<label>Hit window<select class="combat-window" aria-label="Combat hit window"></select></label>' +
    '<div class="cw-creator-buttons"><button class="add-window" type="button">Add hit window here</button><button class="remove-window" type="button">Remove window</button></div>' +
    '<label>Label<input class="combat-label" aria-label="Hit window label" maxlength="48"></label>' +
    '<label>Hand<select class="combat-hand" aria-label="Hit window hand"><option value="right">Right hand</option><option value="left">Left hand</option><option value="both">Both hands</option></select></label>' +
    ['start','hit','end'].map(field=>`<label>${field==='hit'?'Contact':field[0].toUpperCase()+field.slice(1)} frame<input type="number" class="combat-${field}" aria-label="Hit window ${field} frame" min="0" step="1"><button type="button" class="combat-set-${field}">Use playhead</button></label>`).join('') +
    '<small class="combat-state" role="status"></small>' +
    '<div class="cw-creator-section">FILE</div>' +
    '<div class="cw-creator-buttons"><button type="button" class="new">New draft</button><button type="button" class="save">Save draft JSON…</button>' +
    '<button type="button" class="load">Open draft JSON…</button></div>' +
    '<input type="file" class="file" accept=".json,application/json" hidden>' +
    '<small class="status" role="status">No keyframes yet. Changing a pose value sets a keyframe at the current frame.</small>';
  const find = panel.querySelector('[aria-label="Find animation bone"]');
  const picker = panel.querySelector('[aria-label="Animation bone"]');
  const timeline = panel.querySelector('.timeline'), length = panel.querySelector('.length');
  const dockTimeline = dock.querySelector('.dock-timeline'), reel = dock.querySelector('.cw-creator-reel');
  const reelScroll = dock.querySelector('.cw-creator-scroll');
  const resizeHandle = dock.querySelector('.cw-creator-resize');
  const time = panel.querySelector('.time'), status = panel.querySelector('.status'), keys = panel.querySelector('.keys');
  const play = panel.querySelector('.play'), remove = panel.querySelector('.remove');
  const boneList = panel.querySelector('.cw-creator-bone-list');
  const inputs = [...panel.querySelectorAll('input[data-kind]')];
  const file = panel.querySelector('.file');
  const libraryPicker = panel.querySelector('.library-picker'), libraryInfo = panel.querySelector('.library-info');
  function libraryDetails() {
    const entry = libraryEntries.find(item => item.id === libraryPicker.value);
    libraryInfo.textContent = entry ? `${entry.status} · ${(entry.durationFrames / fps).toFixed(2)}s` +
      (entry.usedBy.length ? ` · On this character: ${entry.usedBy.join(', ')}` : ' · Not assigned to this character') +
      (entry.rigSignature !== rigSignature ? ' · Different rig; cannot open here' : '') : 'Save or import a draft to add it to your private library.';
    panel.querySelector('.library-open').disabled = !entry?.available || entry.rigSignature !== rigSignature;
  }
  function refreshLibrary(entries = libraryEntries) {
    libraryEntries = entries; const selected = libraryPicker.value || libraryDraftId;
    const filter = panel.querySelector('.library-filter').value, query = panel.querySelector('.library-search').value.toLowerCase().trim();
    libraryPicker.replaceChildren();
    for (const entry of entries) {
      const displayName = entry.name.replace(/_/g, ' ').replace(/ Retarget$/i, '');
      if (filter === 'used' && !entry.usedBy.length || filter === 'ready' && entry.status !== 'Cooked' ||
        query && !`${entry.name} ${displayName} ${entry.character} ${entry.usedBy.join(' ')}`.toLowerCase().includes(query)) continue;
      const option = document.createElement('option'); option.value = entry.id;
      option.textContent = `${entry.usedBy.length ? '● ' : ''}${displayName} · ${entry.status}`; option.title = entry.name; libraryPicker.appendChild(option);
    }
    if ([...libraryPicker.options].some(option => option.value === selected)) libraryPicker.value = selected;
    libraryDetails();
  }
  libraryPicker.onchange = libraryDetails;
  panel.querySelector('.library-filter').onchange = () => refreshLibrary();
  panel.querySelector('.library-search').oninput = () => refreshLibrary();
  panel.querySelector('.library-open').onclick = async () => {
    const entry = libraryEntries.find(item => item.id === libraryPicker.value); if (!entry?.available) return;
    try {
      const response = await fetch(entry.file + '?v=' + Date.now()); if (!response.ok) throw new Error('Draft source is unavailable.');
      const json = await response.text(); if (new Blob([json]).size > 2000000) throw new Error('Draft exceeds 2 MB.');
      if (await openDraft(JSON.parse(json), entry.name)) { libraryDraftId = entry.id; pose(); }
    } catch (error) { status.textContent = 'Could not open library draft: ' + error.message; }
  };
  panel.querySelector('.library-save').disabled = !window.BATCOMPUTER_ANIMATION_LIBRARY_HOST || !window.chrome?.webview || !window.PREVIEW_CAN_SAVE_PLACEMENTS;
  panel.querySelector('.library-save').onclick = () => {
    const draft = draftDocument();
    if (new Blob([JSON.stringify(draft)]).size > 2000000) { status.textContent = 'Draft exceeds 2 MB.'; return; }
    panel.querySelector('.library-save').disabled = true;
    pendingLibrarySave = {revision,generation:draftGeneration};
    status.textContent = 'Saving to your private animation library…';
    window.chrome.webview.postMessage({type:'save-animation-draft',layoutKey:window.PREVIEW_LAYOUT_KEY,id:libraryDraftId,draft});
  };
  const identity = new THREE.Quaternion();
  let pendingSource=null;
  const sourceButtons=[...panel.querySelectorAll('.source-edit,.source-refresh,.source-pak,.source-blender')];
  const sourceHost=!!(window.BATCOMPUTER_ANIMATION_LIBRARY_HOST&&window.chrome?.webview&&window.PREVIEW_CAN_SAVE_PLACEMENTS&&window.PREVIEW_LAYOUT_KEY?.startsWith('suit:'));
  function fillSources() {
    const picker=panel.querySelector('.source-picker'),old=picker.value,query=panel.querySelector('.source-search').value.toLowerCase().trim();
    picker.replaceChildren();
    for(const asset of preview.catalog?.()||[]) {
      if(/_(LEGOface|HAT|Cape)(_|$)/i.test(asset.name)||query&&!`${asset.name} ${asset.group} ${asset.package}`.toLowerCase().includes(query))continue;
      const option=document.createElement('option');option.value=asset.package;option.textContent=`${asset.name} · ${asset.group||'Native'}`;picker.appendChild(option);
    }
    if([...picker.options].some(option=>option.value===old))picker.value=old;
    panel.querySelector('.source-edit').disabled=!!pendingSource||!picker.value;
    for(const name of ['refresh','pak','blender'])panel.querySelector('.source-'+name).disabled=!!pendingSource||!sourceHost;
    if(!sourceHost)for(const name of ['refresh','pak','blender'])panel.querySelector('.source-'+name).title='Open a saved suit in the embedded viewer to import into its workspace.';
  }
  panel.querySelector('.source-search').oninput=fillSources;
  function requestSource(kind) {
    if(pendingSource||!sourceHost)return;
    playing=false;pendingSource={generation:draftGeneration,revision,kind};fillSources();
    status.textContent=kind==='blender'?'Choose a native-rig Blender file, then an action. Reading without saving the source…':'Reading your cooked animation library…';
    window.chrome.webview.postMessage({type:'import-animation-source',kind,layoutKey:window.PREVIEW_LAYOUT_KEY,rigSignature});
  }
  for(const kind of ['pak','blender'])panel.querySelector('.source-'+kind).onclick=()=>requestSource(kind);
  panel.querySelector('.source-refresh').onclick=()=>requestSource('library');
  panel.querySelector('.source-edit').onclick=async()=>{
    if(pendingSource)return;
    const context={generation:draftGeneration,revision};pendingSource=context;fillSources();playing=false;
    status.textContent='Sampling a copy of the source animation into editable keys…';
    try {
      const draft=await preview.sourceDraft(panel.querySelector('.source-picker').value,rigSignature,rest);
      if(context.generation!==draftGeneration||context.revision!==revision)throw new Error('Your draft changed while loading. Import again when ready.');
      if(await openDraft(draft))libraryDraftId='';
    } catch(error){status.textContent='Could not import motion: '+error.message;}
    finally{pendingSource=null;pose();fillSources();}
  };
  async function blenderDraft(file,name) {
    const source=await new Promise((resolve,reject)=>new THREE.GLTFLoader().load(file,resolve,undefined,reject));
    let mixer;
    try {
      const imported=new Map();
      source.scene.traverse(node=>{if(node.isBone){if(imported.has(node.name))throw new Error('The export has duplicate bones.');imported.set(node.name,node);}});
      const importedSignature=[...imported].map(([bone,node])=>`${bone}:${node.parent?.isBone?node.parent.name:''}`).sort().join('|');
      if(importedSignature!==rigSignature.split('|').sort().join('|'))throw new Error('The Blender export does not retain the complete native bone hierarchy.');
      for(const [bone,node] of imported) {
        const reference=rest.get(bone);
        if(node.position.distanceTo(reference.p)>.0002||node.quaternion.angleTo(reference.q)>.001||node.scale.distanceTo(reference.s)>.001)
          throw new Error(`Rest-space mismatch at ${bone}. Use this viewer's unmodified native GLB rig; do not apply the FBX preparation helper to an animation import.`);
      }
      if(source.animations.length!==1)throw new Error('Expected one exported Blender action. Remove other active object animations and retry.');
      const clip=source.animations[0];
      // Blender often starts actions at frame 1 (or a later scene frame). Preserve
      // clip length, not the leading scene-time gap, in the draft's zero-based timeline.
      const start=Math.min(...clip.tracks.map(track=>track.times[0]));
      if(!Number.isFinite(start)||start<0)throw new Error('The exported action has invalid sample times.');
      // glTF channels can share a time accessor. Copy it before shifting each
      // track, otherwise the common array is shifted repeatedly.
      for(const track of clip.tracks){track.times=Float32Array.from(track.times);track.shift(-start);}
      clip.resetDuration();const duration=Math.round(clip.duration*fps);
      if(duration<3||duration>900)throw new Error('The action must be 0.1–30 seconds.');
      // Object-level travel is not a body bone track. Reject instead of quietly dropping it.
      for(const track of clip.tracks)if(![...imported.keys()].some(bone=>track.name.startsWith(bone+'.')))throw new Error('Animation includes object-level motion. Bake motion onto native bones before importing.');
      mixer=new THREE.AnimationMixer(source.scene);const action=mixer.clipAction(clip);action.setLoop(THREE.LoopOnce,1);action.clampWhenFinished=true;action.play();
      const tracks=[...bones.keys()].map(bone=>({bone,keys:[]}));
      const compact=values=>values.map(value=>Math.round(value*1e8)/1e8);
      for(let at=0;at<=duration;at++) {
        mixer.setTime(Math.min(clip.duration,at/fps));
        for(const track of tracks) {
          const node=imported.get(track.bone),reference=rest.get(track.bone),q=reference.q.clone().invert().multiply(node.quaternion).normalize();
          if(track.keys.length&&q.dot(new THREE.Quaternion(...track.keys.at(-1).q))<0)q.set(-q.x,-q.y,-q.z,-q.w);
          track.keys.push({frame:at,p:compact(node.position.clone().sub(reference.p).toArray()),q:compact(q.toArray()),s:compact(node.scale.clone().divide(reference.s).toArray()),interpolation:'linear'});
        }
      }
      return {schema:'batcomputer.animation-draft.v1',name:name.slice(0,64),description:'Editable copy of a native-rig Blender action; gameplay notifies are not imported.',fps,durationFrames:duration,loop:true,rigSignature,
        tracks:tracks.filter(track=>track.keys.some(key=>key.p.some(v=>Math.abs(v)>1e-5)||key.q.slice(0,3).some(v=>Math.abs(v)>1e-5)||key.s.some(v=>Math.abs(v-1)>1e-5)))};
    } finally {
      mixer?.stopAllAction();mixer?.uncacheRoot(source.scene);
      source.scene.traverse(node=>{node.geometry?.dispose();for(const material of Array.isArray(node.material)?node.material:node.material?[node.material]:[])material.dispose();});
    }
  }
  async function sourceImported(result,error) {
    const context=pendingSource;if(!context)return;
    try {
      if(error)throw new Error(error);
      if(result?.catalog){preview.addCatalog?.(result.catalog);status.textContent=`${result.catalog.length} of your cooked body clips are available above. Choose one, then copy to an editable draft.`;}
      else if(result?.cancelled)status.textContent='Import cancelled. Your current draft is unchanged.';
      else if(result?.file) {
        const draft=await blenderDraft(result.file,result.name||'Blender motion');
        if(context.generation!==draftGeneration||context.revision!==revision)throw new Error('Your draft changed while importing. Import again when ready.');
        if(new Blob([JSON.stringify(draft)]).size>2000000)throw new Error('The sampled draft exceeds 2 MB. Shorten the action.');
        if(await openDraft(draft))libraryDraftId='';
      }
    }catch(failure){status.textContent='Could not import source: '+failure.message;}
    finally{pendingSource=null;pose();fillSources();}
  }
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const rounded = value => Math.abs(value) < .0000005 ? 0 : Number(value.toFixed(4));
  const dockHeightStorageKey = 'batcomputer.animationTimelineHeight';
  let requestedDockHeight = 218;
  try {
    const stored = Number(window.localStorage.getItem(dockHeightStorageKey));
    if (Number.isFinite(stored) && stored >= 160 && stored <= 1200) requestedDockHeight = stored;
  } catch { /* Some embedded browser contexts disable persistent storage. Resizing still works. */ }
  function applyDockHeight(height, persist = false) {
    if (!dock.parentElement) return;
    const maximum = Math.max(160, dock.parentElement.clientHeight - 58);
    const bounded = Math.round(clamp(height, 160, maximum));
    dock.style.height = `${bounded}px`;
    resizeHandle.setAttribute('aria-valuemax', String(maximum));
    resizeHandle.setAttribute('aria-valuenow', String(bounded));
    if (persist) {
      requestedDockHeight = bounded;
      try { window.localStorage.setItem(dockHeightStorageKey, String(bounded)); } catch { /* Storage is optional. */ }
    }
  }
  let resizeStartY = 0, resizeStartHeight = 0, resizingDock = false;
  resizeHandle.addEventListener('pointerdown', event => {
    if (event.button !== 0) return;
    event.preventDefault();
    resizingDock = true; resizeStartY = event.clientY; resizeStartHeight = dock.getBoundingClientRect().height;
    resizeHandle.setPointerCapture(event.pointerId);
    dock.classList.add('resizing');
  });
  resizeHandle.addEventListener('pointermove', event => {
    if (resizingDock) applyDockHeight(resizeStartHeight + resizeStartY - event.clientY);
  });
  const finishResize = event => {
    if (!resizingDock) return;
    resizingDock = false; dock.classList.remove('resizing');
    if (resizeHandle.hasPointerCapture(event.pointerId)) resizeHandle.releasePointerCapture(event.pointerId);
    applyDockHeight(dock.getBoundingClientRect().height, true);
  };
  resizeHandle.addEventListener('pointerup', finishResize);
  resizeHandle.addEventListener('pointercancel', finishResize);
  resizeHandle.addEventListener('keydown', event => {
    const current = dock.getBoundingClientRect().height;
    const change = event.key === 'ArrowUp' ? 32 : event.key === 'ArrowDown' ? -32 : 0;
    if (change) { event.preventDefault(); applyDockHeight(current + change, true); }
    else if (event.key === 'Home') { event.preventDefault(); applyDockHeight(160, true); }
    else if (event.key === 'End') { event.preventDefault(); applyDockHeight(dock.parentElement?.clientHeight || current, true); }
  });
  function snapshot() { return { durationFrames, clipName, clipDescription, loop, sourceAnimationPackage, voiceCues:voiceCues.map(value=>({...value})), selectedVoice, combatWindows:combatWindows.map(value=>({...value})), selectedWindow,
    boneGroups:boneGroups.map(group=>({...group,bones:[...group.bones]})), selectedGroup, selectedBone, selectedBones:[...selectedBones],
    tracks:[...tracks].map(([name, values]) => [name, values.map(key => ({ frame:key.frame, p:[...key.p], q:[...key.q], s:[...(key.s || [1,1,1])], interpolation:key.interpolation || 'linear' }))]) }; }
  function pushUndo() { undoStack.push(snapshot()); if (undoStack.length > 50) undoStack.shift(); redoStack.length = 0; refreshHistory(); }
  function restoreSnapshot(value) {
    durationFrames = value.durationFrames; clipName = value.clipName; clipDescription = value.clipDescription;
    loop=value.loop;dock.querySelector('.loop').checked=loop;
    sourceAnimationPackage = value.sourceAnimationPackage || ''; combatWindows = (value.combatWindows || []).map(item=>({...item})); selectedWindow = value.selectedWindow || '';
    voiceCues = (value.voiceCues || []).map(item=>({...item})); selectedVoice = value.selectedVoice || '';
    boneGroups = value.boneGroups.map(group=>({...group,bones:[...group.bones]})); selectedGroup = value.selectedGroup;
    selectedBone = value.selectedBone; selectedBones = new Set(value.selectedBones); refreshGroups(); fillBones(); attachBone();
    tracks.clear(); for (const [name, values] of value.tracks) tracks.set(name, values);
    panel.querySelector('.clip-name').value = clipName; panel.querySelector('.clip-description').value = clipDescription;
    length.value = String(durationFrames / fps); frame = clamp(frame, 0, durationFrames); playing = false;
    reelDirty = true; pose(); markDirty(); refreshHistory();
  }
  function refreshHistory() { panel.querySelector('.undo').disabled = !undoStack.length; panel.querySelector('.redo').disabled = !redoStack.length; }
  function markDirty() { revision++; dirty = true; keysDirty = true; panel.querySelector('.draft-indicator').textContent = 'Unsaved draft'; }
  const gizmo = new THREE.TransformControls(camera, renderer.domElement);
  gizmo.setSize(.7); gizmo.setSpace('local'); gizmo.setMode('translate'); gizmo.visible = false; scene.add(gizmo);
  const jointGeometry = new THREE.SphereGeometry(.019, 8, 6);
  const jointNormal = new THREE.MeshBasicMaterial({ color:0x56bfff, depthTest:false, transparent:true, opacity:.85 });
  const jointSelected = new THREE.MeshBasicMaterial({ color:0xffda43, depthTest:false });
  const joints = new Map();
  for (const [name] of bones) {
    const marker = new THREE.Mesh(jointGeometry, jointNormal); marker.userData.boneName = name;
    marker.renderOrder = 20; marker.visible = false; scene.add(marker); joints.set(name, marker);
  }
  let showJoints = false;
  let timelineWidth = -1;
  const timelineResize = new ResizeObserver(() => {
    // Height/scrollbar changes do not change the time-to-pixel scale. Do not rebuild
    // thousands of diamonds in response to the layout produced by that rebuild.
    const width = reelScroll.clientWidth;
    if (active && width !== timelineWidth) { timelineWidth = width; reelDirty = true; refresh(); }
  });
  timelineResize.observe(reelScroll);
  const raycaster = new THREE.Raycaster(), pointer = new THREE.Vector2();
  function updateJoints() {
    if (!showJoints || !active) return;
    for (const [name, marker] of joints) {
      marker.position.copy(bones.get(name).getWorldPosition(new THREE.Vector3()));
      marker.material = selectedBones.has(name) ? jointSelected : jointNormal;
    }
  }
  const pickJoint = event => {
    if (!active || !showJoints || gizmo.axis || event.button !== 0) return;
    const rect = renderer.domElement.getBoundingClientRect();
    pointer.set((event.clientX - rect.left) / rect.width * 2 - 1, -(event.clientY - rect.top) / rect.height * 2 + 1);
    raycaster.setFromCamera(pointer, camera);
    const hit = raycaster.intersectObjects([...joints.values()], false)[0];
    if (!hit) return;
    event.stopPropagation(); event.preventDefault();
    selectBone(hit.object.userData.boneName, event);
  };
  renderer.domElement.addEventListener('pointerdown', pickJoint, true);
  function tool(mode) {
    gizmo.setMode(mode);
    panel.querySelector('.move').classList.toggle('active', mode === 'translate');
    panel.querySelector('.rotate').classList.toggle('active', mode === 'rotate');
    panel.querySelector('.scale').classList.toggle('active', mode === 'scale');
  }
  panel.querySelector('.move').onclick = () => tool('translate');
  panel.querySelector('.rotate').onclick = () => tool('rotate');
  panel.querySelector('.scale').onclick = () => tool('scale');
  panel.querySelector('.axes').onchange = event => gizmo.setSpace(event.target.value);
  function updateSnap() {
    const enabled = panel.querySelector('.snap').checked;
    const degrees = clamp(Number(panel.querySelector('.snap-degrees').value) || 15, 1, 90);
    gizmo.setRotationSnap(enabled ? degrees * Math.PI / 180 : null);
    gizmo.setTranslationSnap(enabled ? .05 : null);
    gizmo.setScaleSnap(enabled ? .1 : null);
  }
  panel.querySelector('.snap').onchange = updateSnap;
  panel.querySelector('.snap-degrees').onchange = updateSnap;
  function attachBone() {
    gizmo.detach();
    let anchor = bones.get(selectedBone);
    for (let parent = anchor?.parent; parent?.isBone; parent = parent.parent)
      if (selectedBones.has(parent.name)) anchor = parent;
    if (active && anchor) { gizmo.attach(anchor); gizmo.visible = true; }
    else gizmo.visible = false;
    updateJoints();
  }
  let dragAnchor = null, dragRoots = [];
  gizmo.addEventListener('dragging-changed', event => {
    dragging = event.value; controls.enabled = !event.value;
    if (event.value) {
      playing = false; pushUndo(); root.updateMatrixWorld(true);
      dragAnchor = gizmo.object?.matrixWorld.clone();
      dragRoots = [...selectedBones].map(name=>bones.get(name)).filter(bone=>{
        for(let parent=bone.parent;parent?.isBone;parent=parent.parent)if(selectedBones.has(parent.name))return false;
        return true;
      }).map(bone=>({bone,world:bone.matrixWorld.clone()}));
    } else { dragAnchor = null; dragRoots = []; if (active) pose(); }
  });
  gizmo.addEventListener('objectChange', () => {
    if (!active || !dragging) return;
    if (!dragAnchor || !gizmo.object) return;
    root.updateMatrixWorld(true);
    const delta = gizmo.object.matrixWorld.clone().multiply(dragAnchor.clone().invert());
    for (const {bone,world} of dragRoots) if (bone !== gizmo.object) {
      bone.parent.updateWorldMatrix(true,false);
      const local = bone.parent.matrixWorld.clone().invert().multiply(delta).multiply(world);
      local.decompose(bone.position,bone.quaternion,bone.scale);
    }
    const values = [...selectedBones].map(name=>{
      const bone=bones.get(name),saved=rest.get(name);
      return {name,p:bone.position.clone().sub(saved.p).toArray(),q:saved.q.clone().invert().multiply(bone.quaternion).normalize(),s:bone.scale.clone().divide(saved.s).toArray()};
    });
    if(values.some(value=>!value.p.every(v=>Number.isFinite(v)&&Math.abs(v)<=5)||!value.s.every(v=>Number.isFinite(v)&&v>=.05&&v<=5))) {
      pose(); status.textContent='Transform exceeds the supported position or scale limits.'; return;
    }
    for(const value of values)writeKey(value.name,value.p,value.q,value.s);
    markDirty(); root.updateMatrixWorld(true); preview.followBodyPose?.(false); updateJoints(); refresh();
  });
  window.addEventListener('pagehide', () => {
    timelineResize.disconnect();
    renderer.domElement.removeEventListener('pointerdown', pickJoint, true);
    gizmo.detach(); scene.remove(gizmo); gizmo.dispose();
    for (const marker of joints.values()) scene.remove(marker);
    jointGeometry.dispose(); jointNormal.dispose(); jointSelected.dispose();
  }, { once:true });

  function selectBone(name, event = {}) {
    if (!bones.has(name)) return;
    if(event.shiftKey) {
      const names=[...bones.keys()].filter(item=>!find.value.trim()||item.toLowerCase().includes(find.value.trim().toLowerCase()));
      const a=names.indexOf(selectedBone),b=names.indexOf(name);
      if(a>=0&&b>=0) for(const item of names.slice(Math.min(a,b),Math.max(a,b)+1))selectedBones.add(item);
      else selectedBones.add(name);
    } else if(event.ctrlKey||event.metaKey) {
      if(selectedBones.has(name)&&selectedBones.size>1)selectedBones.delete(name);else selectedBones.add(name);
    } else selectedBones=new Set([name]);
    selectedBone=selectedBones.has(name)?name:[...selectedBones][0];
    selectionChanged();
  }
  function selectionChanged() { keysDirty=true; reelDirty=true; fillBones(); attachBone(); refresh(); }
  panel.querySelector('.select-all-bones').onclick=()=>{selectedBones=new Set(bones.keys());selectionChanged();};
  panel.querySelector('.select-one-bone').onclick=()=>selectBone(selectedBone);
  function refreshGroups() {
    const picker=panel.querySelector('.bone-group');picker.replaceChildren();
    const empty=document.createElement('option');empty.value='';empty.textContent='Choose a group';picker.appendChild(empty);
    for(const group of boneGroups){const option=document.createElement('option');option.value=group.id;option.textContent=`${group.name} · ${group.bones.length} bones`;picker.appendChild(option);}
    if(!boneGroups.some(group=>group.id===selectedGroup))selectedGroup='';picker.value=selectedGroup;
    panel.querySelector('.group-name').value=boneGroups.find(group=>group.id===selectedGroup)?.name||'';
    for(const action of ['assign','select','rename','delete'])panel.querySelector('.group-'+action).disabled=!selectedGroup;
    panel.querySelector('.group-create').disabled=boneGroups.length>=32;
  }
  panel.querySelector('.bone-group').onchange=event=>{selectedGroup=event.target.value;refreshGroups();};
  function groupName(){return panel.querySelector('.group-name').value.trim();}
  function assignGroup(group){for(const item of boneGroups)item.bones=item.bones.filter(name=>!selectedBones.has(name));group.bones=[...selectedBones];}
  panel.querySelector('.group-create').onclick=()=>{
    const name=groupName();if(!name||boneGroups.length>=32){status.textContent='Enter a group name (up to 32 groups).';return;}
    pushUndo();const group={id:'group_'+crypto.randomUUID().replace(/-/g,''),name,bones:[]};boneGroups.push(group);assignGroup(group);selectedGroup=group.id;
    markDirty();reelDirty=true;refreshGroups();refresh();
  };
  panel.querySelector('.group-assign').onclick=()=>{const group=boneGroups.find(item=>item.id===selectedGroup);if(!group)return;pushUndo();assignGroup(group);markDirty();reelDirty=true;refreshGroups();refresh();};
  panel.querySelector('.group-select').onclick=()=>{const group=boneGroups.find(item=>item.id===selectedGroup);if(!group?.bones.length)return;selectedBones=new Set(group.bones);selectedBone=group.bones[0];selectionChanged();};
  panel.querySelector('.group-rename').onclick=()=>{const group=boneGroups.find(item=>item.id===selectedGroup),name=groupName();if(!group||!name)return;pushUndo();group.name=name;markDirty();reelDirty=true;refreshGroups();refresh();};
  panel.querySelector('.group-delete').onclick=()=>{if(!selectedGroup)return;pushUndo();boneGroups=boneGroups.filter(item=>item.id!==selectedGroup);selectedGroup='';markDirty();reelDirty=true;refreshGroups();refresh();status.textContent='Group removed. Its bone tracks are unchanged.';};
  panel.querySelector('.focus-joint').onclick = () => {
    const target = bones.get(selectedBone)?.getWorldPosition(new THREE.Vector3());
    if (!target) return;
    camera.position.add(target.clone().sub(controls.target)); controls.target.copy(target); controls.update();
  };
  panel.querySelector('.toggle-joints').onclick = event => {
    showJoints = !showJoints; event.target.setAttribute('aria-pressed', String(showJoints));
    event.target.textContent = showJoints ? 'Hide joints' : 'Show joints';
    for (const marker of joints.values()) marker.visible = active && showJoints;
    updateJoints();
  };

  function fillBones() {
    const query = find.value.trim().toLowerCase();
    picker.replaceChildren();
    for (const name of bones.keys()) if (!query || name.toLowerCase().includes(query)) {
      const option = document.createElement('option'); option.value = name; option.textContent = name; picker.appendChild(option);
    }
    if (picker.options.length) {
      picker.value = selectedBone;
    } else {
      const option = document.createElement('option'); option.textContent = 'No matching bones'; option.disabled = true;
      picker.appendChild(option);
    }
    picker.disabled = !picker.options.length || picker.options[0].disabled;
    boneList.replaceChildren();
    for (const [name, bone] of bones) {
      if (query && !name.toLowerCase().includes(query)) continue;
      let depth = 0, parent = bone.parent;
      while (parent?.isBone && depth < 8) { depth++; parent = parent.parent; }
      const button = document.createElement('button'); button.type = 'button';
      button.textContent = name; button.style.paddingLeft = `${8 + depth * 9}px`;
      button.className = selectedBones.has(name) ? 'selected' : '';
      button.setAttribute('aria-pressed',String(selectedBones.has(name)));
      button.title = `${name}${bone.parent?.isBone ? ' · parent: ' + bone.parent.name : ''}`;
      button.onclick = event => selectBone(name,event);
      boneList.appendChild(button);
    }
    panel.querySelector('.selection-info').textContent=`${selectedBones.size} selected · primary: ${selectedBone}`;
    refresh();
  }
  const sampled = new Map([...bones.keys()].map(name => [name, {
    p:new THREE.Vector3(), q:new THREE.Quaternion(), s:new THREE.Vector3(1,1,1)
  }]));
  const nextPosition = new THREE.Vector3(), nextRotation = new THREE.Quaternion(), nextScale = new THREE.Vector3();
  const neutralKey = { frame:0, p:[0,0,0], q:[0,0,0,1], s:[1,1,1] };
  function sample(name, at) {
    const track = tracks.get(name) || [];
    const value = sampled.get(name);
    let low = 0, high = track.length;
    while (low < high) { const mid = (low + high) >>> 1; if (track[mid].frame < at) low = mid + 1; else high = mid; }
    const right = track[low], left = track[low - 1];
    const start = right?.frame === at ? right : left || neutralKey;
    value.p.fromArray(start.p); value.q.fromArray(start.q); value.s.fromArray(start.s || neutralKey.s);
    if (!right || right.frame === at || !track.length || at <= 0) return value;
    let mix = clamp((at - start.frame) / (right.frame - start.frame), 0, 1);
    if (start.interpolation === 'hold') mix = 0;
    else if (start.interpolation === 'smooth') mix = mix * mix * (3 - 2 * mix);
    value.p.lerp(nextPosition.fromArray(right.p), mix);
    value.q.slerp(nextRotation.fromArray(right.q), mix);
    value.s.lerp(nextScale.fromArray(right.s || neutralKey.s), mix);
    return value;
  }
  function applyRest() {
    bones.forEach((bone, name) => { const saved = rest.get(name);
      bone.position.copy(saved.p); bone.quaternion.copy(saved.q); bone.scale.copy(saved.s); });
    preview.followBodyPose?.(false);
    root.updateMatrixWorld(true);
    updateJoints();
  }
  function pose() {
    bones.forEach((bone, name) => { const saved = rest.get(name), value = sample(name, frame);
      bone.position.copy(saved.p).add(value.p);
      bone.quaternion.copy(saved.q).multiply(value.q);
      bone.scale.copy(saved.s).multiply(value.s); });
    preview.followBodyPose?.(false);
    updateJoints();
    // Pose evaluation stays continuous. The inspector only needs a new integer
    // timeline frame; editing/scrubbing always refreshes immediately.
    if (!playing || Math.round(frame) !== lastUiFrame) refresh();
  }
  function currentValues() {
    const value = sample(selectedBone, frame);
    const euler = new THREE.Euler().setFromQuaternion(value.q, 'XYZ');
    return { position:value.p.toArray(), rotation:[euler.x, euler.y, euler.z].map(v => v * 180 / Math.PI), scale:value.s.toArray() };
  }
  function refresh() {
    refreshCombat();
    refreshVoice();
    timeline.max = String(durationFrames); timeline.value = String(Math.round(frame));
    dockTimeline.max = String(durationFrames); dockTimeline.value = String(Math.round(frame));
    // Updating an inherited CSS variable on the reel invalidates the computed
    // style of every diamond. Only these small overlay elements actually move.
    const playheadPosition = `${clamp(frame / durationFrames * 100, 0, 100)}%`;
    for (const playhead of playheads) playhead.style.left = playheadPosition;
    const frameNumber = dock.querySelector('.frame-number');
    frameNumber.max = String(durationFrames);
    if (document.activeElement !== frameNumber) frameNumber.value = String(Math.round(frame));
    if (keysDirty || reelDirty) keyCount = [...tracks.values()].reduce((n, track) => n + track.length, 0);
    time.textContent = `${(frame / fps).toFixed(2)} / ${(durationFrames / fps).toFixed(2)} s · frame ${Math.round(frame)} · ${keyCount} keys`;
    dock.querySelector('.dock-time').textContent = `${Math.round(frame)} / ${durationFrames} · ${(frame / fps).toFixed(2)}s`;
    play.textContent = playing ? 'Pause' : 'Play';
    dock.querySelector('.dock-play').textContent = playing ? '❚❚ Pause' : '▶ Play';
    const values = currentValues();
    for (const input of inputs) if (document.activeElement !== input) input.value = String(rounded(values[input.dataset.kind][Number(input.dataset.axis)]));
    const track = tracks.get(selectedBone) || [];
    const currentKey = track.find(key => key.frame === Math.round(frame));
    const selectionHasKey = [...selectedBones].some(name=>(tracks.get(name)||[]).some(key=>key.frame===Math.round(frame)));
    remove.disabled = panel.querySelector('.copy-key').disabled = !selectionHasKey;
    panel.querySelector('.paste-key').disabled = !copiedKey;
    panel.querySelector('.clear-track').disabled = ![...selectedBones].some(name=>tracks.get(name)?.length);
    panel.querySelector('.interpolation').disabled = !selectionHasKey;
    panel.querySelector('.interpolation').value = currentKey?.interpolation || 'linear';
    if (keysDirty || reelDirty) {
    keysDirty = false; keys.replaceChildren(); selectedKeysByFrame.clear();
    for (const key of track) {
      const button = document.createElement('button'); button.type = 'button'; button.textContent = `${(key.frame / fps).toFixed(2)} s`;
      button.className = key.frame === Math.round(frame) ? 'active' : '';
      button.onclick = () => { playing = false; frame = key.frame; pose(); };
      keys.appendChild(button);
      selectedKeysByFrame.set(key.frame, button);
    }
    if (!track.length) keys.textContent = 'No keys on this bone.';
    }
    selectedKeysByFrame.get(lastUiFrame)?.classList.remove('active');
    selectedKeysByFrame.get(Math.round(frame))?.classList.add('active');
    lastUiFrame = Math.round(frame);
    if (reelDirty) refreshReel();
    else if (Math.round(frame) !== lastReelFrame) {
      for (const marker of markersByFrame.get(lastReelFrame) || []) marker.classList.remove('active');
      for (const marker of markersByFrame.get(Math.round(frame)) || []) marker.classList.add('active');
      lastReelFrame = Math.round(frame);
    }
  }
  function refreshReel() {
    reelDirty = false; lastReelFrame = Math.round(frame);
    const visible = [...tracks].filter(([, values]) => values.length).map(([name]) => name);
    for(const name of selectedBones)if(!visible.includes(name))visible.unshift(name);
    reel.replaceChildren(); markersByFrame.clear(); playheads.length = 0;
    const ruler = dock.querySelector('.cw-creator-ruler'); ruler.replaceChildren();
    const railWidth = Math.max(220, reelScroll.clientWidth - 126, durationFrames * 3 * zoom);
    ruler.style.width = `${railWidth + 125}px`;
    for (let second = 0; second <= durationFrames / fps; second++) {
      const tick = document.createElement('span'); tick.textContent = `${second}s`;
      tick.style.left = `${125 + second * fps / durationFrames * railWidth}px`; ruler.appendChild(tick);
    }
    for (const cue of voiceCues) {
      const row=document.createElement('div');row.className='cw-creator-lane';row.style.width=`${railWidth+125}px`;
      const label=document.createElement('button');label.type='button';label.textContent='Voice · '+voiceTags[cue.tag];label.title=voiceTags[cue.tag];label.onclick=()=>{selectedVoice=cue.id;seek(cue.frame);};row.appendChild(label);
      const rail=document.createElement('div');rail.className='cw-creator-lane-rail';row.appendChild(rail);
      const marker=document.createElement('button');marker.type='button';marker.className='cw-combat-marker hit';marker.style.left=`${cue.frame/durationFrames*100}%`;
      marker.textContent='♪';marker.title=`${voiceTags[cue.tag]} · frame ${cue.frame}`;marker.setAttribute('aria-label',marker.title);
      marker.onclick=()=>{selectedVoice=cue.id;seek(cue.frame);};rail.appendChild(marker);
      rail.onclick=event=>{if(event.target===rail)seek(Math.round((event.clientX-rail.getBoundingClientRect().left)/rail.clientWidth*durationFrames));};
      addPlayhead(rail);reel.appendChild(row);
    }
    for (const window of combatWindows) {
      const row = document.createElement('div'); row.className = 'cw-creator-lane cw-combat-lane'; row.style.width = `${railWidth + 125}px`; row.dataset.windowId=window.id;
      const label = document.createElement('button'); label.type='button'; label.textContent=window.name; label.title=`${window.hand} · frames ${window.start}–${window.end}`;
      label.onclick=()=>{selectedWindow=window.id;seek(window.hit);};
      const rail=document.createElement('div');rail.className='cw-creator-lane-rail';
      addPlayhead(rail);
      const range=document.createElement('span');range.className='cw-combat-range';range.style.left=`${window.start/durationFrames*100}%`;range.style.width=`${(window.end-window.start)/durationFrames*100}%`;rail.appendChild(range);
      rail.onclick=event=>{if(event.target===rail||event.target===range)seek(Math.round((event.clientX-rail.getBoundingClientRect().left)/rail.clientWidth*durationFrames));};
      for(const field of ['start','hit','end']) {
        const marker=document.createElement('button');marker.type='button';marker.className='cw-combat-marker '+field;marker.style.left=`${window[field]/durationFrames*100}%`;
        marker.title=`${window.name} ${field} · frame ${window[field]}`;marker.setAttribute('aria-label',marker.title);marker.textContent=field==='hit'?'◆':field==='start'?'[':']';
        marker.onclick=()=>{selectedWindow=window.id;seek(window[field]);};
        marker.onpointerdown=event=>{
          if(event.button!==0)return;event.preventDefault();event.stopPropagation();
          const bounds=rail.getBoundingClientRect(),startX=event.clientX,old=window[field];let destination=old,moved=false;
          const move=pointer=>{if(!moved&&Math.abs(pointer.clientX-startX)<4)return;moved=true;destination=clamp(Math.round((pointer.clientX-bounds.left)/bounds.width*durationFrames),0,durationFrames);marker.style.left=`${destination/durationFrames*100}%`;};
          const up=()=>{globalThis.removeEventListener('pointermove',move);globalThis.removeEventListener('pointerup',up);globalThis.removeEventListener('pointercancel',cancel);
            selectedWindow=window.id;if(moved)changeWindow(field,destination);else seek(old);};
          const cancel=()=>{globalThis.removeEventListener('pointermove',move);globalThis.removeEventListener('pointerup',up);reelDirty=true;refresh();};
          globalThis.addEventListener('pointermove',move);globalThis.addEventListener('pointerup',up,{once:true});globalThis.addEventListener('pointercancel',cancel,{once:true});
        };rail.appendChild(marker);
      }row.append(label,rail);reel.appendChild(row);
    }
    const grouped = new Set(boneGroups.flatMap(group=>group.bones));
    const sections = [...boneGroups.map(group=>({...group,visible:visible.filter(name=>group.bones.includes(name))})),
      {id:'',name:'Ungrouped',visible:visible.filter(name=>!grouped.has(name))}];
    for(const section of sections) {
      if(boneGroups.length && (section.id || section.visible.length)) {
        const header=document.createElement('div');header.className='cw-creator-group-header';
        const collapse=document.createElement('button');collapse.type='button';collapse.textContent=`${collapsedGroups.has(section.id)?'▸':'▾'} ${section.name}`;
        collapse.setAttribute('aria-expanded',String(!collapsedGroups.has(section.id)));
        collapse.onclick=()=>{if(collapsedGroups.has(section.id))collapsedGroups.delete(section.id);else collapsedGroups.add(section.id);reelDirty=true;refresh();};
        header.appendChild(collapse);reel.appendChild(header);
      }
      if(collapsedGroups.has(section.id))continue;
    for (const name of section.visible) {
      const row = document.createElement('div'); row.className = 'cw-creator-lane' + (selectedBones.has(name) ? ' selected' : '');
      row.style.width = `${railWidth + 125}px`;
      const label = document.createElement('button'); label.type = 'button'; label.textContent = name; label.title = `Select ${name}`;
      label.onclick = event => selectBone(name,event);
      const rail = document.createElement('div'); rail.className = 'cw-creator-lane-rail';
      addPlayhead(rail);
      rail.onclick = event => { if (event.target !== rail) return; seek(Math.round((event.clientX - rail.getBoundingClientRect().left) / rail.clientWidth * durationFrames)); };
      for (const key of tracks.get(name) || []) {
        const marker = document.createElement('button'); marker.type = 'button'; marker.className = 'cw-key-marker' + (key.frame === Math.round(frame) ? ' active' : '');
        marker.title = `${name} · frame ${key.frame}`; marker.setAttribute('aria-label', marker.title); marker.style.left = `${key.frame / durationFrames * 100}%`;
        marker.dataset.frame = String(key.frame);
        if (!markersByFrame.has(key.frame)) markersByFrame.set(key.frame, []);
        markersByFrame.get(key.frame).push(marker);
        marker.onclick = () => { selectBone(name); seek(key.frame); };
        marker.onpointerdown = event => {
          if (event.button !== 0) return;
          event.stopPropagation();
          const startX = event.clientX, originalFrame = key.frame;
          const bounds = rail.getBoundingClientRect();
          let destination = originalFrame, moved = false;
          const move = pointerEvent => {
            if (Math.abs(pointerEvent.clientX - startX) < 4 && !moved) return;
            moved = true;
            destination = clamp(Math.round((pointerEvent.clientX - bounds.left) / bounds.width * durationFrames), 0, durationFrames);
            marker.style.left = `${destination / durationFrames * 100}%`;
            marker.title = `${name} · move to frame ${destination}`;
          };
          const up = () => {
            window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up);
            if (!moved) { selectBone(name); seek(originalFrame); return; }
            const values = tracks.get(name) || [];
            if (destination !== originalFrame && values.some(item => item !== key && item.frame === destination)) {
              status.textContent = `Frame ${destination} already has a ${name} key. Choose an empty frame.`;
            } else if (destination !== originalFrame) {
              pushUndo(); key.frame = destination; values.sort((a, b) => a.frame - b.frame);
              markDirty(); status.textContent = `Moved ${name} key to frame ${destination}.`;
            }
            selectBone(name); frame = destination; reelDirty = true; pose();
          };
          window.addEventListener('pointermove', move); window.addEventListener('pointerup', up, { once:true });
        };
        rail.appendChild(marker);
      }
      row.append(label, rail); reel.appendChild(row);
    }
    }
  }
  function addPlayhead(rail) {
    const playhead = document.createElement('span'); playhead.className = 'cw-creator-playhead';
    playhead.style.left = `${clamp(frame / durationFrames * 100, 0, 100)}%`; rail.appendChild(playhead); playheads.push(playhead);
  }
  function seek(value) { playing = false; frame = clamp(value, 0, durationFrames); pose(); }
  function validateVoice(value,duration) {
    if(value===undefined)return [];
    if(value?.schema!=='batcomputer.voice-cues.v1'||!Array.isArray(value.cues)||value.cues.length>16)throw new Error('Invalid voice cue metadata.');
    const ids=new Set(),frames=new Set();return value.cues.map(cue=>{
      if(typeof cue.id!=='string'||!/^[a-zA-Z0-9_-]{1,64}$/.test(cue.id)||ids.has(cue.id)||!Object.hasOwn(voiceTags,cue.tag)||!Number.isInteger(cue.frame)||cue.frame<0||cue.frame>=duration||frames.has(cue.frame)||
        (cue.playOnEmptySwing!==undefined&&typeof cue.playOnEmptySwing!=='boolean')||(cue.playOnEmptySwing===true&&cue.tag==='VOX_Jump'))
        throw new Error('Voice cues need unique IDs and frames, a supported category and a frame before the end.');
      ids.add(cue.id);frames.add(cue.frame);return {id:cue.id,tag:cue.tag,frame:cue.frame,playOnEmptySwing:cue.playOnEmptySwing===true};
    });
  }
  function refreshVoice() {
    const picker=panel.querySelector('.voice-picker'),item=voiceCues.find(cue=>cue.id===selectedVoice);
    const labels=voiceCues.map(cue=>`${voiceTags[cue.tag]} · frame ${cue.frame}`);
    if(picker.options.length!==voiceCues.length||[...picker.options].some((option,index)=>option.value!==voiceCues[index].id||option.textContent!==labels[index])) {
      picker.replaceChildren();voiceCues.forEach((cue,index)=>{const option=document.createElement('option');option.value=cue.id;option.textContent=labels[index];picker.appendChild(option);});
    }
    picker.value=selectedVoice;picker.disabled=!voiceCues.length;
    for(const field of ['tag','frame']){const input=panel.querySelector('.voice-'+field);input.disabled=!item;if(document.activeElement!==input)input.value=item?item[field]:'';}
    panel.querySelector('.voice-frame').max=String(durationFrames-1);
    panel.querySelector('.voice-empty').disabled=!item||item.tag==='VOX_Jump';panel.querySelector('.voice-empty').checked=item?.playOnEmptySwing===true;
    panel.querySelector('.voice-remove').disabled=!item;panel.querySelector('.voice-playhead').disabled=!item||Math.round(frame)>=durationFrames;
    panel.querySelector('.voice-add').disabled=voiceCues.length>=16||Math.round(frame)>=durationFrames||voiceCues.some(cue=>cue.frame===Math.round(frame));
  }
  function changeVoice(field,value) {
    const item=voiceCues.find(cue=>cue.id===selectedVoice);if(!item)return;
    const candidate={...item,[field]:value};if(candidate.tag==='VOX_Jump')candidate.playOnEmptySwing=false;
    try{validateVoice({schema:'batcomputer.voice-cues.v1',cues:voiceCues.map(cue=>cue===item?candidate:cue)},durationFrames);}
    catch(error){status.textContent=error.message;if(field!=='playOnEmptySwing')panel.querySelector('.voice-'+field).value=item[field];refreshVoice();return;}
    pushUndo();Object.assign(item,candidate);markDirty();reelDirty=true;playing=false;pose();
  }
  panel.querySelector('.voice-picker').onchange=event=>{selectedVoice=event.target.value;refresh();};
  panel.querySelector('.voice-add').onclick=()=>{
    if(panel.querySelector('.voice-add').disabled)return;
    pushUndo();const id='voice_'+Date.now().toString(36)+'_'+voiceCues.length;voiceCues.push({id,tag:'VOX_Combat',frame:Math.round(frame)});selectedVoice=id;markDirty();reelDirty=true;pose();
  };
  panel.querySelector('.voice-remove').onclick=()=>{if(!selectedVoice)return;pushUndo();voiceCues=voiceCues.filter(cue=>cue.id!==selectedVoice);selectedVoice=voiceCues[0]?.id||'';markDirty();reelDirty=true;pose();};
  panel.querySelector('.voice-tag').onchange=event=>changeVoice('tag',event.target.value);
  panel.querySelector('.voice-frame').onchange=event=>changeVoice('frame',Number(event.target.value));
  panel.querySelector('.voice-playhead').onclick=()=>changeVoice('frame',Math.round(frame));
  panel.querySelector('.voice-empty').onchange=event=>changeVoice('playOnEmptySwing',event.target.checked);
  function validateCombat(value, duration) {
    if(value===undefined)return [];
    if(value?.schema!=='batcomputer.combat-timing.v1'||value.previewOnly!==true||!Array.isArray(value.windows)||value.windows.length>16)
      throw new Error('Invalid preview combat timing metadata.');
    const ids=new Set();return value.windows.map(item=>{
      if(typeof item.id!=='string'||!/^[a-zA-Z0-9_-]{1,64}$/.test(item.id)||ids.has(item.id)||typeof item.name!=='string'||!item.name.trim()||item.name.length>48||
        !['left','right','both'].includes(item.hand)||![item.start,item.hit,item.end].every(Number.isInteger)||item.start<0||item.start>=item.end||item.hit<item.start||item.hit>item.end||item.end>duration)
        throw new Error('Hit windows need unique IDs and start ≤ contact ≤ end within the clip.');
      ids.add(item.id);return {id:item.id,name:item.name,start:item.start,hit:item.hit,end:item.end,hand:item.hand};
    });
  }
  function refreshCombat() {
    const picker=panel.querySelector('.combat-window'),item=combatWindows.find(value=>value.id===selectedWindow);
    if(picker.options.length!==combatWindows.length||[...picker.options].some((option,index)=>option.value!==combatWindows[index].id||option.textContent!==combatWindows[index].name)) {
      picker.replaceChildren();for(const value of combatWindows){const option=document.createElement('option');option.value=value.id;option.textContent=value.name;picker.appendChild(option);}
    }
    picker.value=selectedWindow;picker.disabled=!combatWindows.length;panel.querySelector('.remove-window').disabled=!item;panel.querySelector('.add-window').disabled=combatWindows.length>=16||Math.round(frame)>=durationFrames;
    for(const field of ['start','hit','end','label','hand']) {
      const input=panel.querySelector('.combat-'+field);input.disabled=!item;
      if(document.activeElement!==input)input.value=item?(field==='label'?item.name:item[field]):'';
      if(['start','hit','end'].includes(field)){input.max=String(durationFrames);panel.querySelector('.combat-set-'+field).disabled=!item;}
    }
    const activeWindows=combatWindows.filter(value=>frame>=value.start&&frame<=value.end);
    panel.querySelector('.combat-state').textContent=activeWindows.length?'Active preview: '+activeWindows.map(value=>`${value.name} · ${value.hand}${Math.round(frame)===value.hit?' · CONTACT':''}`).join(', '):'No active hit window at this frame. Preview markers only.';
    for(const marker of reel.querySelectorAll('.cw-combat-range')) marker.classList.toggle('active',activeWindows.some(value=>marker.parentElement.parentElement.dataset.windowId===value.id));
  }
  function changeWindow(field,value) {
    const item=combatWindows.find(item=>item.id===selectedWindow);if(!item)return;
    const candidate={...item,[field==='label'?'name':field]:value};
    try{validateCombat({schema:'batcomputer.combat-timing.v1',previewOnly:true,windows:[candidate]},durationFrames);}
    catch(error){panel.querySelector('.combat-'+field).value=field==='label'?item.name:item[field];status.textContent=error.message;reelDirty=true;refresh();return;}
    pushUndo();Object.assign(item,candidate);markDirty();reelDirty=true;playing=false;pose();
  }
  panel.querySelector('.combat-window').onchange=event=>{selectedWindow=event.target.value;refresh();};
  panel.querySelector('.add-window').onclick=()=>{
    const start=Math.round(frame);if(start>=durationFrames||combatWindows.length>=16)return;
    pushUndo();const end=Math.min(durationFrames,start+6),id='hit_'+Date.now().toString(36)+'_'+combatWindows.length;
    combatWindows.push({id,name:'Hit '+(combatWindows.length+1),start,hit:Math.round((start+end)/2),end,hand:'right'});selectedWindow=id;markDirty();reelDirty=true;pose();
  };
  panel.querySelector('.remove-window').onclick=()=>{if(!selectedWindow)return;pushUndo();combatWindows=combatWindows.filter(item=>item.id!==selectedWindow);selectedWindow=combatWindows[0]?.id||'';markDirty();reelDirty=true;pose();};
  for(const field of ['start','hit','end']) {
    panel.querySelector('.combat-'+field).onchange=event=>changeWindow(field,Number(event.target.value));
    panel.querySelector('.combat-set-'+field).onclick=()=>changeWindow(field,Math.round(frame));
  }
  panel.querySelector('.combat-label').onchange=event=>changeWindow('label',event.target.value.trim());
  panel.querySelector('.combat-hand').onchange=event=>changeWindow('hand',event.target.value);
  function writeKey(name, p, q, s = [1,1,1], interpolation) {
    const at = Math.round(frame), track = tracks.get(name) || [];
    const existing = track.findIndex(item => item.frame === at);
    const key = { frame:at, p:p.map(rounded), q:q.toArray(), s:s.map(rounded), interpolation:interpolation || (existing >= 0 ? track[existing].interpolation || 'linear' : 'linear') };
    if (existing >= 0) track[existing] = key; else track.push(key);
    track.sort((a, b) => a.frame - b.frame); tracks.set(name, track);
    if (existing < 0) reelDirty = true;
  }
  function editSelection(edit, message='Updated selected bones.') {
    pushUndo(); playing=false;
    for(const name of selectedBones)edit(name);
    markDirty(); reelDirty=true;pose();status.textContent=message;
  }
  inputs.forEach(input => input.onchange = () => {
    if (!Number.isFinite(Number(input.value))) { refresh(); return; }
    playing = false;
    const values = currentValues(),kind=input.dataset.kind,axis=Number(input.dataset.axis);
    const next = kind === 'scale'
      ? clamp(Number(input.value), .05, 5)
      : clamp(Number(input.value), kind === 'rotation' ? -180 : -5, kind === 'rotation' ? 180 : 5);
    const change=next-values[kind][axis];
    const edits=[...selectedBones].map(name=>{
      const value=sample(name,frame),euler=new THREE.Euler().setFromQuaternion(value.q,'XYZ');
      const channels={position:value.p.toArray(),rotation:[euler.x,euler.y,euler.z].map(v=>v*180/Math.PI),scale:value.s.toArray()};
      channels[kind][axis]+=change;
      return {name,...channels,q:new THREE.Quaternion().setFromEuler(new THREE.Euler(...channels.rotation.map(v=>v*Math.PI/180),'XYZ'))};
    });
    if(edits.some(value=>!value.position.every(v=>Math.abs(v)<=5)||!value.scale.every(v=>v>=.05&&v<=5))){refresh();status.textContent='One selected bone would exceed the position or scale limits.';return;}
    pushUndo();for(const value of edits)writeKey(value.name,value.position,value.q,value.scale);
    markDirty();pose();status.textContent=`Keyed ${selectedBones.size} selected bone(s).`;
  });
  find.oninput = fillBones;
  picker.onchange = () => selectBone(picker.value);
  timeline.oninput = () => seek(Number(timeline.value));
  dockTimeline.oninput = () => seek(Number(dockTimeline.value));
  dock.querySelector('.frame-number').onchange = event => seek(Math.round(Number(event.target.value) || 0));
  dock.querySelector('.speed').onchange = event => { playbackRate = Number(event.target.value) || 1; };
  dock.querySelector('.zoom').onchange = event => { zoom = Number(event.target.value) || 1; reelDirty = true; refresh(); };
  window.addEventListener('resize', () => { if (active) { reelDirty = true; refresh(); } });
  dock.querySelector('.first').onclick = () => seek(0);
  dock.querySelector('.previous').onclick = () => seek(Math.round(frame) - 1);
  dock.querySelector('.next').onclick = () => seek(Math.round(frame) + 1);
  dock.querySelector('.last').onclick = () => seek(durationFrames);
  dock.querySelector('.dock-play').onclick = () => play.click();
  dock.querySelector('.loop').onchange = event => { loop = event.target.checked; };
  length.onchange = () => {
    const requested = Math.round(Number(length.value) * fps);
    const lastKey = Math.max(0, ...voiceCues.map(item=>item.frame+1), ...combatWindows.map(item=>item.end), ...[...tracks.values()].flatMap(track => track.map(key => key.frame)));
    if (!Number.isInteger(requested) || requested < 3 || requested > 900 || requested < lastKey) {
      length.value = String(durationFrames / fps);
      status.textContent = 'Length must be 0.1–30 seconds and include every existing keyframe.'; return;
    }
    if (durationFrames !== requested) { pushUndo(); durationFrames = requested; markDirty(); reelDirty = true; }
    frame = Math.min(frame, durationFrames); pose();
  };
  panel.querySelector('.clip-name').onchange = event => {
    const value = event.target.value.trim() || 'New animation';
    if (value !== clipName) { pushUndo(); clipName = value; markDirty(); }
    event.target.value = clipName;
  };
  panel.querySelector('.clip-description').onchange = event => {
    const value = event.target.value.trim();
    if (value !== clipDescription) { pushUndo(); clipDescription = value; markDirty(); }
    event.target.value = clipDescription;
  };
  play.onclick = () => { if (frame >= durationFrames) frame = 0;
    playing = !playing; last = performance.now(); if (active) pose(); else refresh(); };
  panel.querySelector('.key').onclick = () => editSelection(name=>{const value=sample(name,frame);writeKey(name,value.p.toArray(),value.q,value.s.toArray());},`Keyed ${selectedBones.size} selected bone(s).`);
  remove.onclick = () => editSelection(name=>{tracks.set(name,(tracks.get(name)||[]).filter(key=>key.frame!==Math.round(frame)));},'Selected keys removed.');
  panel.querySelector('.interpolation').onchange = event => {
    const interpolation=event.target.value;
    editSelection(name=>{const key=(tracks.get(name)||[]).find(item=>item.frame===Math.round(frame));if(key)key.interpolation=interpolation;});
  };
  panel.querySelector('.copy-key').onclick = () => {
    copiedKey=[...selectedBones].flatMap(name=>{const key=(tracks.get(name)||[]).find(item=>item.frame===Math.round(frame));return key?[{bone:name,p:[...key.p],q:[...key.q],s:[...(key.s||[1,1,1])],interpolation:key.interpolation||'linear'}]:[];});
    if(!copiedKey.length)copiedKey=null;
    refresh(); status.textContent='Copied selected keys. Multiple keys paste back onto the same bones; a single key can paste onto another selection.';
  };
  panel.querySelector('.paste-key').onclick = () => {
    if (!copiedKey) return;
    pushUndo();
    const values=copiedKey.length===1?[...selectedBones].map(bone=>({...copiedKey[0],bone})):copiedKey;
    for(const key of values)writeKey(key.bone,key.p,new THREE.Quaternion(...key.q),key.s,key.interpolation);
    markDirty();reelDirty=true;pose();status.textContent=`Pasted ${values.length} key(s).`;
  };
  panel.querySelector('.reset-bone').onclick = () => {
    editSelection(name=>writeKey(name,[0,0,0],identity.clone(),[1,1,1]),'Keyed selected bones at their rest pose.');
  };
  panel.querySelector('.clear-track').onclick = () => {
    editSelection(name=>tracks.delete(name),'Cleared selected tracks. Undo restores them.');
  };
  panel.querySelector('.undo').onclick = () => {
    if (!undoStack.length) return;
    redoStack.push(snapshot()); restoreSnapshot(undoStack.pop()); status.textContent = 'Undid the last edit.';
  };
  panel.querySelector('.redo').onclick = () => {
    if (!redoStack.length) return;
    undoStack.push(snapshot()); restoreSnapshot(redoStack.pop()); status.textContent = 'Redid the edit.';
  };
  panel.querySelector('.new').onclick = () => {
    if (dirty && !window.confirm('Discard the unsaved animation draft and start a new one?')) return;
    tracks.clear(); draftGeneration++; libraryDraftId = ''; undoStack.length = 0; redoStack.length = 0; copiedKey = null; refreshHistory();
    combatWindows=[];selectedWindow='';sourceAnimationPackage='';
    voiceCues=[];selectedVoice='';
    clipName = 'New animation'; clipDescription = ''; durationFrames = 60; frame = 0; playing = false; loop = true;
    dock.querySelector('.loop').checked = true;
    selectedBone = bones.has('Chest') ? 'Chest' : [...bones.keys()][0];
    selectedBones=new Set([selectedBone]);boneGroups=[];selectedGroup='';collapsedGroups.clear();refreshGroups();
    panel.querySelector('.clip-name').value = clipName; panel.querySelector('.clip-description').value = '';
    length.value = '2'; find.value = ''; fillBones(); attachBone(); tool('translate');
    dirty = false; panel.querySelector('.draft-indicator').textContent = 'New draft';
    reelDirty = true; pose(); status.textContent = 'New rest-pose draft. Choose a bone and frame to start keying.';
  };
  function draftDocument() {
    clipName = panel.querySelector('.clip-name').value.trim() || clipName;
    clipDescription = panel.querySelector('.clip-description').value.trim();
    return { schema:'batcomputer.animation-draft.v1', name:clipName, description:clipDescription, fps, durationFrames, loop, rigSignature,
      ...(libraryDraftId ? {libraryDraftId} : {}),
      sourceAnimationPackage, combatTiming:{schema:'batcomputer.combat-timing.v1',previewOnly:true,windows:combatWindows.map(item=>({...item}))},
      voiceCues:{schema:'batcomputer.voice-cues.v1',cues:voiceCues.map(item=>({...item}))},
      boneGroups:{schema:'batcomputer.bone-groups.v1',groups:boneGroups.map(group=>({...group,bones:[...group.bones]}))},
      tracks:[...tracks].filter(([, keys]) => keys.length).map(([bone, keys]) => ({ bone, keys })) };
  }
  panel.querySelector('.save').onclick = () => {
    const draft = draftDocument();
    const json = JSON.stringify(draft, (_key, value) => typeof value === 'number' ? Math.round(value * 1e8) / 1e8 : value);
    const blob = new Blob([json], { type:'application/json' });
    if (blob.size > 2000000) {
      status.textContent = 'Draft exceeds the 2 MB import/cook limit. Shorten the clip or remove unused bone keys before saving.';
      return;
    }
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a'); link.href = url;
    link.download = (clipName.replace(/[^a-z0-9_-]+/gi, '_').replace(/^_+|_+$/g, '') || 'Batcomputer-animation') + '.animation-draft.json'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    dirty = false; panel.querySelector('.draft-indicator').textContent = 'Saved draft';
    status.textContent = `Saved ${draft.tracks.length} bone track(s) as a draft JSON. This is not a cooked game animation.`;
  };
  panel.querySelector('.load').onclick = () => file.click();
  async function openDraft(draft, fallbackName='Imported animation') {
      const importedGroups=validateGroups(draft.boneGroups);
      const importedCombat=validateCombat(draft.combatTiming,draft.durationFrames);
      const importedVoice=validateVoice(draft.voiceCues,draft.durationFrames);
      if(draft.loop!==undefined&&typeof draft.loop!=='boolean')throw new Error('Invalid loop setting.');
      if (draft.schema !== 'batcomputer.animation-draft.v1' || draft.rigSignature !== rigSignature || draft.fps !== fps ||
          !Number.isInteger(draft.durationFrames) || draft.durationFrames < 3 || draft.durationFrames > 900 ||
          !Array.isArray(draft.tracks) || draft.tracks.length > bones.size)
        throw new Error('The draft does not match this native rig or supported timeline.');
      const imported = new Map();
      for (const track of draft.tracks) {
        if (!bones.has(track.bone) || imported.has(track.bone) || !Array.isArray(track.keys) || track.keys.length > 901)
          throw new Error('The draft contains an invalid bone track.');
        let previous = -1;
        const keys = track.keys.map(key => {
          if (!Number.isInteger(key.frame) || key.frame <= previous || key.frame > draft.durationFrames || key.frame < 0 ||
              !Array.isArray(key.p) || key.p.length !== 3 || !Array.isArray(key.q) || key.q.length !== 4 ||
              !key.p.every(value => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= 5) ||
              !key.q.every(value => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= 1.01) ||
              key.s !== undefined && (!Array.isArray(key.s) || key.s.length !== 3 ||
                !key.s.every(value => typeof value === 'number' && Number.isFinite(value) && value >= .05 && value <= 5)) ||
              key.interpolation && !['linear','smooth','hold'].includes(key.interpolation))
            throw new Error('The draft contains an invalid keyframe.');
          previous = key.frame;
          const q = new THREE.Quaternion(...key.q);
          if (Math.abs(q.length() - 1) > .01) throw new Error('The draft contains an invalid rotation.');
          return { frame:key.frame, p:key.p, q:q.normalize().toArray(), s:key.s || [1,1,1], interpolation:key.interpolation || 'linear' };
        });
        imported.set(track.bone, keys);
      }
      if (dirty && !window.confirm('Replace the unsaved animation draft with this file?')) return false;
      tracks.clear(); imported.forEach((keys, bone) => tracks.set(bone, keys));
      boneGroups=importedGroups;selectedGroup='';collapsedGroups.clear();refreshGroups();
      combatWindows=importedCombat;selectedWindow=combatWindows[0]?.id||'';sourceAnimationPackage=typeof draft.sourceAnimationPackage==='string'?draft.sourceAnimationPackage:'';
      voiceCues=importedVoice;selectedVoice=voiceCues[0]?.id||'';
      durationFrames = draft.durationFrames; length.value = String(durationFrames / fps); frame = 0; playing = false;
      loop = draft.loop === true; dock.querySelector('.loop').checked = loop;
      clipName = typeof draft.name === 'string' ? draft.name.slice(0,64) : fallbackName;
      clipDescription = typeof draft.description === 'string' ? draft.description.slice(0,160) : '';
      panel.querySelector('.clip-name').value = clipName;
      panel.querySelector('.clip-description').value = clipDescription;
      dirty = false; panel.querySelector('.draft-indicator').textContent = 'Loaded draft';
      draftGeneration++;
      libraryDraftId = typeof draft.libraryDraftId === 'string' ? draft.libraryDraftId : '';
      undoStack.length = 0; redoStack.length = 0; refreshHistory(); reelDirty = true;
      selectedBone = draft.tracks[0]?.bone || selectedBone;selectedBones=new Set([selectedBone]); find.value = ''; fillBones();
      if (active) { pose(); attachBone(); } else refresh();
      status.textContent = `Loaded ${imported.size} bone track(s). Draft only; no game assets changed.`;
      return true;
  }
  function validateGroups(value) {
    if(value===undefined)return [];
    if(value?.schema!=='batcomputer.bone-groups.v1'||!Array.isArray(value.groups)||value.groups.length>32)throw new Error('Invalid bone groups.');
    const ids=new Set(),members=new Set();
    return value.groups.map(group=>{
      if(typeof group.id!=='string'||!/^[a-zA-Z0-9_-]{1,64}$/.test(group.id)||ids.has(group.id)||typeof group.name!=='string'||!group.name.trim()||group.name.length>48||!Array.isArray(group.bones)||group.bones.length>bones.size)
        throw new Error('Groups need unique IDs, a name, and native bone names.');
      ids.add(group.id);
      for(const name of group.bones){if(!bones.has(name)||members.has(name))throw new Error('A bone can belong to only one group.');members.add(name);}
      return {id:group.id,name:group.name.trim(),bones:[...group.bones]};
    });
  }
  file.onchange = async () => {
    try { if(!file.files?.length)return;if(file.files[0].size>2000000)throw new Error('Draft must be smaller than 2 MB.');
      await openDraft(JSON.parse(await file.files[0].text()),file.files[0].name.replace(/\.json$/i,''));
    } catch (error) { status.textContent = 'Could not open draft: ' + error.message; }
    finally { file.value = ''; }
  };
  panel.querySelector('.from-motion').onclick=async()=>{
    try{await openDraft(preview.editableBodyDraft(rigSignature,rest));pose();}
    catch(error){status.textContent='Could not edit Motion clip: '+error.message;}
  };
  const keydown = event => {
    if (!active || /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName) || event.target.isContentEditable || dragging || event.altKey) return;
    const key = event.key.toLowerCase();
    if (event.ctrlKey || event.metaKey) {
      if (key === 'z') { event.preventDefault(); panel.querySelector(event.shiftKey ? '.redo' : '.undo').click(); }
      else if (key === 'y') { event.preventDefault(); panel.querySelector('.redo').click(); }
      else if (key === 's') { event.preventDefault(); panel.querySelector(panel.querySelector('.library-save').disabled ? '.save' : '.library-save').click(); }
      else if (key === 'a') { event.preventDefault(); panel.querySelector('.select-all-bones').click(); }
      return;
    }
    if (key === ' ') { event.preventDefault(); play.click(); }
    else if (key === 'arrowleft') { event.preventDefault(); seek(Math.round(frame) - (event.shiftKey ? 5 : 1)); }
    else if (key === 'arrowright') { event.preventDefault(); seek(Math.round(frame) + (event.shiftKey ? 5 : 1)); }
    else if (key === 'home') { event.preventDefault(); seek(0); }
    else if (key === 'end') { event.preventDefault(); seek(durationFrames); }
    else if (key === 'w') { event.preventDefault(); tool('translate'); }
    else if (key === 'r') { event.preventDefault(); tool('rotate'); }
    else if (key === 'e') { event.preventDefault(); tool('scale'); }
    else if (key === 'k') { event.preventDefault(); panel.querySelector('.key').click(); }
    else if (key === 'f') { event.preventDefault(); panel.querySelector('.focus-joint').click(); }
    else if (key === 'j') { event.preventDefault(); panel.querySelector('.toggle-joints').click(); }
    else if (key === 'delete') { event.preventDefault(); remove.click(); }
  };
  window.addEventListener('keydown', keydown);
  window.addEventListener('pagehide', () => window.removeEventListener('keydown', keydown), { once:true });
  refreshGroups();fillBones(); refreshHistory(); refreshLibrary();fillSources();
  return { panel, dock, gizmo,
    sourceImported,
    async openLibraryDraft(id) { const entry = libraryEntries.find(item => item.id === id); if (!entry) return;
      libraryPicker.value = id; await panel.querySelector('.library-open').onclick(); },
    librarySaved(id, entries, error) {
      panel.querySelector('.library-save').disabled = false;
      if (error) { status.textContent = 'Could not save draft: ' + error; return; }
      refreshLibrary(entries || []);
      if (pendingLibrarySave?.generation !== draftGeneration) { status.textContent = 'The previous draft was saved privately. This new draft is unchanged.'; return; }
      libraryDraftId = id;
      if (pendingLibrarySave.revision === revision) { dirty = false; panel.querySelector('.draft-indicator').textContent = 'Saved to your animations'; }
      status.textContent = dirty ? 'Saved the requested version. Your newer edits still need saving.' : 'Saved privately to Your animations. Cooking and character assignment are separate steps.';
    },
    enter() { active = true; preview.rest(); document.querySelector('#cw-viewport')?.appendChild(dock); dock.hidden = false;
      applyDockHeight(requestedDockHeight);
      window.characterMeshEditor?.select(null); reelDirty = true; pose(); attachBone();
      for (const marker of joints.values()) marker.visible = showJoints; updateJoints(); },
    leave() { if (!active) return; active = false; playing = false; dragging = false; controls.enabled = true;
      gizmo.detach(); gizmo.visible = false; dock.hidden = true;
      for (const marker of joints.values()) marker.visible = false; preview.rest(); },
    update() { if (!active || !playing) return;
      const now = performance.now(); frame += (now - last) / 1000 * fps * playbackRate; last = now;
      if (frame >= durationFrames) { if (loop) frame %= durationFrames; else { frame = durationFrames; playing = false; } }
      pose(); },
    withRestPose() { if (!active) return null;
      const savedFrame = frame, wasPlaying = playing, gizmoWasVisible = gizmo.visible;
      playing = false; applyRest(); gizmo.visible = false;
      for (const marker of joints.values()) marker.visible = false;
      return () => { frame = savedFrame; playing = wasPlaying; last = performance.now();
        gizmo.visible = gizmoWasVisible;
        for (const marker of joints.values()) marker.visible = showJoints;
        pose(); }; }
  };
};
