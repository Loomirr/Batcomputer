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
  let clipName = 'New animation', clipDescription = '', playbackRate = 1, zoom = 1, dirty = false, reelDirty = true;
  let copiedKey = null, lastReelFrame = -1;
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
    '<div class="cw-creator-section">CLIP</div>' +
    '<label>Name<input class="clip-name" aria-label="Animation clip name" maxlength="64" value="New animation"></label>' +
    '<label>Purpose<input class="clip-description" aria-label="Animation clip description" maxlength="160" placeholder="e.g. relaxed idle, wave, victory…"></label>' +
    '<label>Length · seconds<input type="number" class="length" aria-label="Animation length" min="0.1" max="30" step="0.1" value="2"></label>' +
    '<small>30 frames per second · 0.1–30 seconds. The timeline below the character controls playback.</small>' +
    '<div class="cw-creator-section">RIG · SELECT A JOINT</div>' +
    '<label>Find bone<input type="search" aria-label="Find animation bone" placeholder="Search bones…"></label>' +
    '<label>Bone<select aria-label="Animation bone"></select></label>' +
    '<div class="cw-creator-bone-list" aria-label="Skeleton bone hierarchy"></div>' +
    '<div class="cw-creator-buttons"><button type="button" class="focus-joint">Focus joint</button><button type="button" class="toggle-joints" aria-pressed="false">Show joints</button></div>' +
    '<div class="cw-creator-section">TRANSFORM · SELECTED BONE</div>' +
    '<div class="cw-creator-buttons cw-creator-tools"><button type="button" class="move active">Move · W</button><button type="button" class="rotate">Rotate · R</button><button type="button" class="scale">Scale · E</button></div>' +
    '<label>Axes <select class="axes" aria-label="Transform axes"><option value="local">Local to bone</option><option value="world">World</option></select></label>' +
    '<label>Snap <span class="cw-creator-inline"><input type="checkbox" class="snap" aria-label="Snap transforms"><input type="number" class="snap-degrees" aria-label="Rotation snap degrees" min="1" max="90" value="15">°</span></label>' +
    '<small>Move, rotate, or scale the 3D handles at this bone’s joint. Changes create a key on the current frame. Position is an offset in viewer units; scale is a multiplier of the rest pose.</small>' +
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
  const identity = new THREE.Quaternion();
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
  function snapshot() { return { durationFrames, clipName, clipDescription,
    tracks:[...tracks].map(([name, values]) => [name, values.map(key => ({ frame:key.frame, p:[...key.p], q:[...key.q], s:[...(key.s || [1,1,1])], interpolation:key.interpolation || 'linear' }))]) }; }
  function pushUndo() { undoStack.push(snapshot()); if (undoStack.length > 50) undoStack.shift(); redoStack.length = 0; refreshHistory(); }
  function restoreSnapshot(value) {
    durationFrames = value.durationFrames; clipName = value.clipName; clipDescription = value.clipDescription;
    tracks.clear(); for (const [name, values] of value.tracks) tracks.set(name, values);
    panel.querySelector('.clip-name').value = clipName; panel.querySelector('.clip-description').value = clipDescription;
    length.value = String(durationFrames / fps); frame = clamp(frame, 0, durationFrames); playing = false;
    reelDirty = true; pose(); markDirty(); refreshHistory();
  }
  function refreshHistory() { panel.querySelector('.undo').disabled = !undoStack.length; panel.querySelector('.redo').disabled = !redoStack.length; }
  function markDirty() { dirty = true; panel.querySelector('.draft-indicator').textContent = 'Unsaved draft'; }
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
  const timelineResize = new ResizeObserver(() => { if (active) { reelDirty = true; refresh(); } });
  timelineResize.observe(reelScroll);
  const raycaster = new THREE.Raycaster(), pointer = new THREE.Vector2();
  function updateJoints() {
    if (!showJoints || !active) return;
    for (const [name, marker] of joints) {
      marker.position.copy(bones.get(name).getWorldPosition(new THREE.Vector3()));
      marker.material = name === selectedBone ? jointSelected : jointNormal;
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
    selectBone(hit.object.userData.boneName);
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
    if (active && bones.has(selectedBone)) { gizmo.attach(bones.get(selectedBone)); gizmo.visible = true; }
    else gizmo.visible = false;
    updateJoints();
  }
  gizmo.addEventListener('dragging-changed', event => {
    dragging = event.value; controls.enabled = !event.value;
    if (event.value) { playing = false; pushUndo(); }
    else if (active) pose();
  });
  gizmo.addEventListener('objectChange', () => {
    if (!active || !dragging) return;
    const bone = bones.get(selectedBone), saved = rest.get(selectedBone);
    if (!bone || !saved) return;
    const p = bone.position.clone().sub(saved.p), q = saved.q.clone().invert().multiply(bone.quaternion).normalize();
    const s = bone.scale.clone().divide(saved.s);
    if (p.length() > 5) { bone.position.copy(saved.p).add(sample(selectedBone, frame).p); return; }
    if (![s.x, s.y, s.z].every(value => Number.isFinite(value) && value >= .05 && value <= 5)) {
      bone.scale.copy(saved.s).multiply(sample(selectedBone, frame).s); return;
    }
    setKey(p.toArray(), q, s.toArray(), false);
  });
  window.addEventListener('pagehide', () => {
    timelineResize.disconnect();
    renderer.domElement.removeEventListener('pointerdown', pickJoint, true);
    gizmo.detach(); scene.remove(gizmo); gizmo.dispose();
    for (const marker of joints.values()) scene.remove(marker);
    jointGeometry.dispose(); jointNormal.dispose(); jointSelected.dispose();
  }, { once:true });

  function selectBone(name) {
    if (!bones.has(name)) return;
    selectedBone = name; find.value = ''; fillBones(); attachBone(); reelDirty = true; refresh();
  }
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
    const previousSelection = selectedBone;
    picker.replaceChildren();
    for (const name of bones.keys()) if (!query || name.toLowerCase().includes(query)) {
      const option = document.createElement('option'); option.value = name; option.textContent = name; picker.appendChild(option);
    }
    if (picker.options.length) {
      if (![...picker.options].some(option => option.value === selectedBone)) selectedBone = picker.options[0].value;
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
      button.className = name === selectedBone ? 'selected' : '';
      button.title = `${name}${bone.parent?.isBone ? ' · parent: ' + bone.parent.name : ''}`;
      button.onclick = () => selectBone(name);
      boneList.appendChild(button);
    }
    if (previousSelection !== selectedBone) { reelDirty = true; attachBone(); }
    refresh();
  }
  function sample(name, at) {
    const track = tracks.get(name) || [];
    if (!track.length || at <= 0 && track[0].frame > 0) return { p:new THREE.Vector3(), q:identity.clone(), s:new THREE.Vector3(1,1,1) };
    const right = track.find(key => key.frame >= at);
    const left = track.filter(key => key.frame <= at).at(-1);
    if (!right) return { p:new THREE.Vector3(...track.at(-1).p), q:new THREE.Quaternion(...track.at(-1).q), s:new THREE.Vector3(...(track.at(-1).s || [1,1,1])) };
    if (left && left.frame === right.frame) return { p:new THREE.Vector3(...left.p), q:new THREE.Quaternion(...left.q), s:new THREE.Vector3(...(left.s || [1,1,1])) };
    const start = left || { frame:0, p:[0,0,0], q:[0,0,0,1], s:[1,1,1] };
    let mix = clamp((at - start.frame) / (right.frame - start.frame), 0, 1);
    if (start.interpolation === 'hold') mix = 0;
    else if (start.interpolation === 'smooth') mix = mix * mix * (3 - 2 * mix);
    return { p:new THREE.Vector3(...start.p).lerp(new THREE.Vector3(...right.p), mix),
      q:new THREE.Quaternion(...start.q).slerp(new THREE.Quaternion(...right.q), mix),
      s:new THREE.Vector3(...(start.s || [1,1,1])).lerp(new THREE.Vector3(...(right.s || [1,1,1])), mix) };
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
    root.updateMatrixWorld(true);
    updateJoints();
    refresh();
  }
  function currentValues() {
    const value = sample(selectedBone, frame);
    const euler = new THREE.Euler().setFromQuaternion(value.q, 'XYZ');
    return { position:value.p.toArray(), rotation:[euler.x, euler.y, euler.z].map(v => v * 180 / Math.PI), scale:value.s.toArray() };
  }
  function refresh() {
    timeline.max = String(durationFrames); timeline.value = String(Math.round(frame));
    dockTimeline.max = String(durationFrames); dockTimeline.value = String(Math.round(frame));
    reel.style.setProperty('--playhead', `${clamp(frame / durationFrames * 100, 0, 100)}%`);
    const frameNumber = dock.querySelector('.frame-number');
    frameNumber.max = String(durationFrames);
    if (document.activeElement !== frameNumber) frameNumber.value = String(Math.round(frame));
    time.textContent = `${(frame / fps).toFixed(2)} / ${(durationFrames / fps).toFixed(2)} s · frame ${Math.round(frame)} · ${[...tracks.values()].reduce((n, track) => n + track.length, 0)} keys`;
    dock.querySelector('.dock-time').textContent = `${Math.round(frame)} / ${durationFrames} · ${(frame / fps).toFixed(2)}s`;
    play.textContent = playing ? 'Pause' : 'Play';
    dock.querySelector('.dock-play').textContent = playing ? '❚❚ Pause' : '▶ Play';
    const values = currentValues();
    for (const input of inputs) input.value = String(rounded(values[input.dataset.kind][Number(input.dataset.axis)]));
    const track = tracks.get(selectedBone) || [];
    const currentKey = track.find(key => key.frame === Math.round(frame));
    remove.disabled = panel.querySelector('.copy-key').disabled = !currentKey;
    panel.querySelector('.paste-key').disabled = !copiedKey;
    panel.querySelector('.clear-track').disabled = !track.length;
    panel.querySelector('.interpolation').disabled = !currentKey;
    panel.querySelector('.interpolation').value = currentKey?.interpolation || 'linear';
    keys.replaceChildren();
    for (const key of track) {
      const button = document.createElement('button'); button.type = 'button'; button.textContent = `${(key.frame / fps).toFixed(2)} s`;
      button.className = key.frame === Math.round(frame) ? 'active' : '';
      button.onclick = () => { playing = false; frame = key.frame; pose(); };
      keys.appendChild(button);
    }
    if (!track.length) keys.textContent = 'No keys on this bone.';
    if (reelDirty) refreshReel();
    else if (Math.round(frame) !== lastReelFrame) {
      for (const marker of reel.querySelectorAll('.cw-key-marker'))
        marker.classList.toggle('active', Number(marker.dataset.frame) === Math.round(frame));
      lastReelFrame = Math.round(frame);
    }
  }
  function refreshReel() {
    reelDirty = false; lastReelFrame = Math.round(frame);
    const visible = [...tracks].filter(([, values]) => values.length).map(([name]) => name);
    if (!visible.includes(selectedBone)) visible.unshift(selectedBone);
    reel.replaceChildren();
    const ruler = dock.querySelector('.cw-creator-ruler'); ruler.replaceChildren();
    const railWidth = Math.max(220, reelScroll.clientWidth - 126, durationFrames * 3 * zoom);
    ruler.style.width = `${railWidth + 125}px`;
    for (let second = 0; second <= durationFrames / fps; second++) {
      const tick = document.createElement('span'); tick.textContent = `${second}s`;
      tick.style.left = `${125 + second * fps / durationFrames * railWidth}px`; ruler.appendChild(tick);
    }
    for (const name of visible) {
      const row = document.createElement('div'); row.className = 'cw-creator-lane' + (name === selectedBone ? ' selected' : '');
      row.style.width = `${railWidth + 125}px`;
      const label = document.createElement('button'); label.type = 'button'; label.textContent = name; label.title = `Select ${name}`;
      label.onclick = () => selectBone(name);
      const rail = document.createElement('div'); rail.className = 'cw-creator-lane-rail';
      rail.onclick = event => { if (event.target !== rail) return; seek(Math.round((event.clientX - rail.getBoundingClientRect().left) / rail.clientWidth * durationFrames)); };
      for (const key of tracks.get(name) || []) {
        const marker = document.createElement('button'); marker.type = 'button'; marker.className = 'cw-key-marker' + (key.frame === Math.round(frame) ? ' active' : '');
        marker.title = `${name} · frame ${key.frame}`; marker.setAttribute('aria-label', marker.title); marker.style.left = `${key.frame / durationFrames * 100}%`;
        marker.dataset.frame = String(key.frame);
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
            selectedBone = name; find.value = ''; fillBones(); attachBone(); frame = destination; reelDirty = true; pose();
          };
          window.addEventListener('pointermove', move); window.addEventListener('pointerup', up, { once:true });
        };
        rail.appendChild(marker);
      }
      row.append(label, rail); reel.appendChild(row);
    }
  }
  function seek(value) { playing = false; frame = clamp(value, 0, durationFrames); pose(); }
  function setKey(p, q, s = [1,1,1], record = true) {
    if (record) pushUndo();
    const at = Math.round(frame), track = tracks.get(selectedBone) || [];
    const existing = track.findIndex(item => item.frame === at);
    const key = { frame:at, p:p.map(rounded), q:q.toArray(), s:s.map(rounded), interpolation:existing >= 0 ? track[existing].interpolation || 'linear' : 'linear' };
    if (existing >= 0) track[existing] = key; else track.push(key);
    track.sort((a, b) => a.frame - b.frame); tracks.set(selectedBone, track);
    markDirty(); if (existing < 0) reelDirty = true;
    pose();
    status.textContent = `Keyed ${selectedBone} at ${(at / fps).toFixed(2)} s.`;
  }
  inputs.forEach(input => input.onchange = () => {
    if (!Number.isFinite(Number(input.value))) { refresh(); return; }
    playing = false;
    const values = currentValues();
    const kind = input.dataset.kind;
    values[kind][Number(input.dataset.axis)] = kind === 'scale'
      ? clamp(Number(input.value), .05, 5)
      : clamp(Number(input.value), kind === 'rotation' ? -180 : -5, kind === 'rotation' ? 180 : 5);
    const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(...values.rotation.map(v => v * Math.PI / 180), 'XYZ'));
    setKey(values.position, q, values.scale);
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
    const lastKey = Math.max(0, ...[...tracks.values()].flatMap(track => track.map(key => key.frame)));
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
  panel.querySelector('.key').onclick = () => { const value = sample(selectedBone, frame); setKey(value.p.toArray(), value.q, value.s.toArray()); };
  remove.onclick = () => { const track = tracks.get(selectedBone) || [];
    if (!track.some(key => key.frame === Math.round(frame))) return;
    pushUndo(); tracks.set(selectedBone, track.filter(key => key.frame !== Math.round(frame)));
    markDirty(); reelDirty = true; pose(); status.textContent = 'Keyframe removed.'; };
  panel.querySelector('.interpolation').onchange = event => {
    const key = (tracks.get(selectedBone) || []).find(item => item.frame === Math.round(frame));
    if (!key || key.interpolation === event.target.value) return;
    pushUndo(); key.interpolation = event.target.value; markDirty(); reelDirty = true; pose();
  };
  panel.querySelector('.copy-key').onclick = () => {
    const key = (tracks.get(selectedBone) || []).find(item => item.frame === Math.round(frame));
    if (!key) return;
    copiedKey = { p:[...key.p], q:[...key.q], s:[...(key.s || [1,1,1])], interpolation:key.interpolation || 'linear' };
    refresh(); status.textContent = `Copied ${selectedBone} key at frame ${key.frame}. Select a bone/frame and paste.`;
  };
  panel.querySelector('.paste-key').onclick = () => {
    if (!copiedKey) return;
    setKey([...copiedKey.p], new THREE.Quaternion(...copiedKey.q), [...copiedKey.s]);
    const key = (tracks.get(selectedBone) || []).find(item => item.frame === Math.round(frame));
    key.interpolation = copiedKey.interpolation;
    reelDirty = true; refresh(); status.textContent = `Pasted key on ${selectedBone} at frame ${Math.round(frame)}.`;
  };
  panel.querySelector('.reset-bone').onclick = () => {
    setKey([0,0,0], identity.clone(), [1,1,1]); status.textContent = `Keyed ${selectedBone} at its rest pose.`;
  };
  panel.querySelector('.clear-track').onclick = () => {
    if (!(tracks.get(selectedBone) || []).length) return;
    pushUndo(); tracks.delete(selectedBone); markDirty(); reelDirty = true; pose();
    status.textContent = `Cleared the ${selectedBone} track. Undo restores it.`;
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
    tracks.clear(); undoStack.length = 0; redoStack.length = 0; copiedKey = null; refreshHistory();
    clipName = 'New animation'; clipDescription = ''; durationFrames = 60; frame = 0; playing = false; loop = true;
    dock.querySelector('.loop').checked = true;
    selectedBone = bones.has('Chest') ? 'Chest' : [...bones.keys()][0];
    panel.querySelector('.clip-name').value = clipName; panel.querySelector('.clip-description').value = '';
    length.value = '2'; find.value = ''; fillBones(); attachBone(); tool('translate');
    dirty = false; panel.querySelector('.draft-indicator').textContent = 'New draft';
    reelDirty = true; pose(); status.textContent = 'New rest-pose draft. Choose a bone and frame to start keying.';
  };
  panel.querySelector('.save').onclick = () => {
    clipName = panel.querySelector('.clip-name').value.trim() || clipName;
    clipDescription = panel.querySelector('.clip-description').value.trim();
    const draft = { schema:'batcomputer.animation-draft.v1', name:clipName, description:clipDescription, fps, durationFrames, loop, rigSignature,
      tracks:[...tracks].filter(([, keys]) => keys.length).map(([bone, keys]) => ({ bone, keys })) };
    const url = URL.createObjectURL(new Blob([JSON.stringify(draft, null, 2)], { type:'application/json' }));
    const link = document.createElement('a'); link.href = url;
    link.download = (clipName.replace(/[^a-z0-9_-]+/gi, '_').replace(/^_+|_+$/g, '') || 'Batcomputer-animation') + '.animation-draft.json'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    dirty = false; panel.querySelector('.draft-indicator').textContent = 'Saved draft';
    status.textContent = `Saved ${draft.tracks.length} bone track(s) as a draft JSON. This is not a cooked game animation.`;
  };
  panel.querySelector('.load').onclick = () => file.click();
  file.onchange = async () => {
    try {
      if (!file.files?.length) return;
      const draft = JSON.parse(await file.files[0].text());
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
              ![...key.p, ...key.q].every(value => typeof value === 'number' && Number.isFinite(value) && Math.abs(value) <= 10) ||
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
      if (dirty && !window.confirm('Replace the unsaved animation draft with this file?')) return;
      tracks.clear(); imported.forEach((keys, bone) => tracks.set(bone, keys));
      durationFrames = draft.durationFrames; length.value = String(durationFrames / fps); frame = 0; playing = false;
      loop = draft.loop === true; dock.querySelector('.loop').checked = loop;
      clipName = typeof draft.name === 'string' ? draft.name.slice(0,64) : file.files[0].name.replace(/\.json$/i,'');
      clipDescription = typeof draft.description === 'string' ? draft.description.slice(0,160) : '';
      panel.querySelector('.clip-name').value = clipName;
      panel.querySelector('.clip-description').value = clipDescription;
      dirty = false; panel.querySelector('.draft-indicator').textContent = 'Loaded draft';
      undoStack.length = 0; redoStack.length = 0; refreshHistory(); reelDirty = true;
      selectedBone = draft.tracks[0]?.bone || selectedBone; find.value = ''; fillBones();
      if (active) { pose(); attachBone(); } else refresh();
      status.textContent = `Loaded ${imported.size} bone track(s). Draft only; no game assets changed.`;
    } catch (error) { status.textContent = 'Could not open draft: ' + error.message; }
    finally { file.value = ''; }
  };
  const keydown = event => {
    if (!active || /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName) || event.target.isContentEditable || dragging || event.altKey) return;
    const key = event.key.toLowerCase();
    if (event.ctrlKey || event.metaKey) {
      if (key === 'z') { event.preventDefault(); panel.querySelector(event.shiftKey ? '.redo' : '.undo').click(); }
      else if (key === 'y') { event.preventDefault(); panel.querySelector('.redo').click(); }
      else if (key === 's') { event.preventDefault(); panel.querySelector('.save').click(); }
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
  fillBones(); refreshHistory();
  return { panel, dock, gizmo,
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
