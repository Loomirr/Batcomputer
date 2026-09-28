// Read-only character-family clip preview. Native skeleton tracks are sampled by the host;
// the game AnimBlueprint, root motion, cloth, physics and facial graph are not run here.
window.BatcomputerCharacterAnimationPreview = function ({ THREE, loaded, root, data }) {
  const panel = document.createElement('section'); panel.id = 'cw-animations';
  const initialClips = (data?.clips || []).filter(c => c.frameCount > 1 && c.framesPerSecond > 0 && c.tracks);
  const catalog = data?.catalog?.length ? data.catalog : initialClips.map(c => ({ name:c.label, group:'Movement', package:c.package }));
  const cache = new Map(initialClips.map(c => [c.package, c]));
  const bundleCache = new Map();
  const initialPackages = new Set(cache.keys());
  const body = loaded.find(entry => entry.m.part === 'CharacterMesh0');
  const bones = new Map(); body?.scene.traverse(node => { if (node.isBone && !bones.has(node.name)) bones.set(node.name, node); });
  const available = body && data?.rig?.length && catalog.length && bones.size;
  if (!available) {
    panel.textContent = 'No character-family animation sequences or compatible body rig were found.';
    return { panel, update() {}, rest() {}, withRestPose() { return null; } };
  }

  // frameAll recenters root immediately before this panel is constructed. Updating only the
  // child leaves its matrixWorld in the old frame, so the first pose teleports every follower.
  root.updateMatrixWorld(true);
  function createContext(entry, rigData) {
    const localBones = new Map(); entry.scene.traverse(node => { if (node.isBone && !localBones.has(node.name)) localBones.set(node.name, node); });
    if (!localBones.size || !rigData?.length) return null;
    const localRig = rigData.map(item => ({ ...item, reference:matrix(item.reference) }));
    const index = new Map(localRig.map((item, i) => [item.name, i]));
    const rest = new Map(); localBones.forEach((bone, name) => rest.set(name, {
      p:bone.position.clone(), q:bone.quaternion.clone(), s:bone.scale.clone() }));
    const rigRest = [];
    localRig.forEach((item, i) => { rigRest[i] = item.parent >= 0
      ? new THREE.Matrix4().multiplyMatrices(rigRest[item.parent], item.reference) : item.reference.clone(); });
    const inTarget = node => entry.scene.matrixWorld.clone().invert().multiply(node.matrixWorld);
    const previewRest = new Map();
    const captureReference = () => { root.updateMatrixWorld(true);
      localBones.forEach((bone, name) => previewRest.set(name, inTarget(bone))); };
    // The face viewer starts from a sampled neutral expression, not the mesh's bind pose.
    // Animation tracks are authored against bind; applying their delta to neutral doubles the
    // mouth/brow deformation and can push the print outside the cowl.
    if (entry.m.part === 'Face' && window.BatcomputerFaceAnimationCurves?.withBindPose)
      window.BatcomputerFaceAnimationCurves.withBindPose(captureReference);
    else captureReference();
    return { entry, bones:localBones, rig:localRig, index, rest, inTarget, previewRest,
      rigInverse:rigRest.map(value => value.clone().invert()),
      ordered:[...localBones].filter(([name]) => index.has(name)).sort((a, b) => index.get(a[0]) - index.get(b[0])) };
  }
  const bodyContext = createContext(body, data.rig);
  const contexts = new Map([['CharacterMesh0', bodyContext]]);
  function contextFor(value) {
    const part = value.targetPart || 'CharacterMesh0';
    if (contexts.has(part)) return contexts.get(part);
    const entry = loaded.find(item => item.m.part === part);
    const context = entry && createContext(entry, value.rig);
    if (context) contexts.set(part, context);
    return context;
  }
  const anchorFor = entry => entry.m.part === '__BareHead'
    ? (bones.has('Head_Attach_01') ? 'Head_Attach_01' : 'Head')
    : entry.m.anchor;
  const followers = loaded.filter(entry => entry !== body && !entry.m.beside && bones.has(anchorFor(entry))).map(entry => {
    const bone = bones.get(anchorFor(entry));
    return { entry, bone, p: entry.scene.position.clone(), q: entry.scene.quaternion.clone(), s: entry.scene.scale.clone(),
      world: entry.scene.matrixWorld.clone(), inverse: bone.matrixWorld.clone().invert() };
  });
  const separate = loaded.filter(entry => entry.m.beside);
  let separateBeforeMotion = null;
  let faceCurvesActive = false;
  function hideSeparate() {
    separateBeforeMotion ??= separate.map(entry => entry.scene.visible);
    separate.forEach(entry => { entry.scene.visible = false; });
  }
  function restoreSeparate() {
    if (separateBeforeMotion) separate.forEach((entry, i) => { entry.scene.visible = separateBeforeMotion[i]; });
    separateBeforeMotion = null;
  }

  panel.innerHTML = '<small class="catalog-count"></small><input type="search" aria-label="Search animations" placeholder="Search animations…">' +
    '<label>Animation<select aria-label="Base-game animation"></select></label>' +
    '<div class="cw-animation-buttons"><button type="button" class="play">Play</button><button type="button" class="rest">Rest pose</button></div>' +
    '<input type="range" min="0" max="1000" value="0" aria-label="Animation position"><small class="time"></small>' +
    '<label>Speed<select aria-label="Animation speed"><option value=".25">0.25×</option><option value=".5">0.5×</option><option value="1" selected>1×</option><option value="2">2×</option></select></label>' +
    '<label><input type="checkbox" checked> Loop</label><small class="status" role="status"></small><small class="note"></small>';
  const picker = panel.querySelector('[aria-label="Base-game animation"]'), scrub = panel.querySelector('[aria-label="Animation position"]');
  const search = panel.querySelector('[aria-label="Search animations"]'), status = panel.querySelector('.status');
  const speed = panel.querySelector('[aria-label="Animation speed"]'), loop = panel.querySelector('input[type=checkbox]');
  const play = panel.querySelector('.play'), restButton = panel.querySelector('.rest');
  const timeLabel = panel.querySelector('.time'), note = panel.querySelector('.note');
  panel.querySelector('.catalog-count').textContent = `${catalog.length} animation assets in this character family · select one to preview`;
  // An initial clip may be preloaded for quick selection, but the assembly opens in
  // its authored rest pose with every normally visible part still present.
  let selectedPackage = '';
  let clip = null, linkedClips = [], time = 0, playing = false,
    last = 0, pending = null, error = '', linkedWarnings = [];
  function fillPicker() {
    picker.replaceChildren(); const groups = new Map(), term = search.value.trim().toLowerCase();
    for (const asset of catalog) {
      if (term && !`${asset.name} ${asset.group} ${asset.package}`.toLowerCase().includes(term)) continue;
      const group = asset.group || 'Other';
      if (!groups.has(group)) { const element = document.createElement('optgroup'); element.label = group;
        picker.appendChild(element); groups.set(group, element); }
      const option = document.createElement('option'); option.value = asset.package; option.textContent = asset.name;
      groups.get(group).appendChild(option);
    }
    if (!picker.options.length) { const option = document.createElement('option'); option.textContent = 'No matching animations';
      option.disabled = true; picker.appendChild(option); }
    picker.value = selectedPackage;
    if (picker.selectedIndex < 0 && picker.options.length && !picker.options[0].disabled) {
      const placeholder = document.createElement('option'); placeholder.textContent = 'Rest pose · choose an animation…';
      placeholder.value = ''; placeholder.disabled = true; picker.prepend(placeholder); picker.value = '';
    }
  }
  function durationOf(value) { return Math.max(.001, (value.frameCount - 1) / value.framesPerSecond); }
  function duration() { return clip ? durationOf(clip) : 0; }
  function sync() {
    play.textContent = playing ? 'Pause' : 'Play';
    play.disabled = scrub.disabled = !clip;
    scrub.value = String(clip ? Math.round(time / duration() * 1000) : 0);
    timeLabel.textContent = clip ? `${time.toFixed(2)} / ${duration().toFixed(2)} s` : '—';
    status.textContent = pending ? 'Loading synchronized tracks from the installed game…' : error ||
      (linkedClips.length ? `Synchronized: ${linkedClips.map(item => item.targetPart).join(', ')}` : '');
    note.textContent = !selectedPackage ? 'Rest pose. Select an animation to preview it; all assembled parts remain visible.' :
      `${selectedPackage.split('/').pop()} · ${clip?.targetPart || 'body'} rig. ` +
      (clip?.sampledSequence ? `Montage preview shows its first sequence (${clip.sampledSequence.split('/').pop()}) only. ` : '') +
      (linkedWarnings.length ? `${linkedWarnings.length} linked track(s) unavailable. ` : '') +
      'Linked parts share the timeline; root motion, full montages and game animation graphs are not simulated.';
  }
  function reset() {
    contexts.forEach(context => context.bones.forEach((bone, name) => { const saved = context.rest.get(name);
      bone.position.copy(saved.p); bone.quaternion.copy(saved.q); bone.scale.copy(saved.s); }));
    followers.forEach(item => { item.entry.scene.position.copy(item.p); item.entry.scene.quaternion.copy(item.q); item.entry.scene.scale.copy(item.s); });
    if (faceCurvesActive) { window.BatcomputerFaceAnimationCurves?.restore(); faceCurvesActive = false; }
  }
  function followBodyPose(nativeCape = false) {
    root.updateMatrixWorld(true);
    followers.forEach(item => {
      // A native cape clip moves its own rig; authored body poses need the root follower.
      if (nativeCape && item.entry.m.part === 'Cape') return;
      const world = new THREE.Matrix4().multiplyMatrices(item.bone.matrixWorld, item.inverse).multiply(item.world);
      new THREE.Matrix4().copy(item.entry.scene.parent.matrixWorld).invert().multiply(world)
        .decompose(item.entry.scene.position, item.entry.scene.quaternion, item.entry.scene.scale);
    });
    root.updateMatrixWorld(true);
  }
  function pose(seconds) {
    if (!clip) return;
    reset();
    hideSeparate();
    const entries = [clip, ...linkedClips].sort((a, b) =>
      (a.targetPart === 'CharacterMesh0' ? -1 : 0) - (b.targetPart === 'CharacterMesh0' ? -1 : 0));
    for (const entry of entries) poseOne(entry, seconds / duration() * durationOf(entry));
  }
  function poseOne(value, seconds) {
    const context = contextFor(value);
    if (!context) return;
    const { rig, index, bones:targetBones, ordered, rigInverse, previewRest, inTarget } = context;
    const locals = rig.map(entry => entry.reference), f = Math.min(seconds * value.framesPerSecond, value.frameCount - 1);
    const a = Math.floor(f), b = Math.min(a + 1, value.frameCount - 1), mix = f - a;
    for (const track of value.tracks) {
      const i = index.get(track.bone); if (i === undefined || !targetBones.has(track.bone)) continue;
      const left = track.frames[a], right = track.frames[b]; if (!left || !right) continue;
      const q = new THREE.Quaternion(left[3], left[4], left[5], left[6]).slerp(
        new THREE.Quaternion(right[3], right[4], right[5], right[6]), mix);
      const lerp = n => left[n] + (right[n] - left[n]) * mix;
      locals[i] = matrix([lerp(0), lerp(1), lerp(2), q.x, q.y, q.z, q.w, lerp(7), lerp(8), lerp(9)]);
    }
    const worlds = [];
    rig.forEach((entry, i) => { worlds[i] = entry.parent >= 0
      ? new THREE.Matrix4().multiplyMatrices(worlds[entry.parent], locals[i]) : locals[i].clone(); });
    const posed = new Map();
    for (const [name, bone] of ordered) {
      const i = index.get(name);
      const target = new THREE.Matrix4().multiplyMatrices(worlds[i], rigInverse[i]).multiply(previewRest.get(name));
      const parent = posed.get(bone.parent) || inTarget(bone.parent);
      new THREE.Matrix4().copy(parent).invert().multiply(target).decompose(bone.position, bone.quaternion, bone.scale);
      posed.set(bone, target);
    }
    root.updateMatrixWorld(true);
    if (context === bodyContext) followBodyPose([clip, ...linkedClips].some(track => track?.targetPart === 'Cape'));
    if (value.targetPart === 'Face' && value.materialCurves?.length && window.BatcomputerFaceAnimationCurves) {
      const left = value.materialCurves[a] || {}, right = value.materialCurves[b] || left, curves = {};
      for (const [key, current] of Object.entries(left))
        curves[key] = current + ((right[key] ?? current) - current) * mix;
      window.BatcomputerFaceAnimationCurves.apply(curves);
      faceCurvesActive = true;
    }
  }
  function matrix(value) { return new THREE.Matrix4().compose(
    new THREE.Vector3(value[0], value[1], value[2]),
    new THREE.Quaternion(value[3], value[4], value[5], value[6]).normalize(),
    new THREE.Vector3(value[7], value[8], value[9])); }
  function select(packageName) {
    if (!packageName || packageName === selectedPackage) return;
    playing = false; reset(); restoreSeparate(); root.updateMatrixWorld(true);
    selectedPackage = packageName; clip = cache.get(packageName) || null;
    linkedClips = bundleCache.get(packageName)?.companions || [];
    linkedWarnings = bundleCache.get(packageName)?.warnings || [];
    time = 0; error = ''; pending = null;
    if (clip) pose(0);
    if (!bundleCache.has(packageName)) requestBundle(packageName);
    sync();
  }
  function requestBundle(packageName) {
    if (window.chrome?.webview) { pending = packageName;
      window.chrome.webview.postMessage({ type:'preview-character-animation', package:packageName }); }
    else if (!clip) error = 'Open this preview inside Batcomputer to load this sequence on demand.';
  }
  fillPicker();
  search.oninput = fillPicker;
  picker.onchange = () => select(picker.value);
  function bundleResult(packageName, result, failure) {
    if (packageName !== pending) return;
    pending = null; error = failure || '';
    const primary = result?.primary;
    // A linked rig may arrive while the body is playing. Capture its rest matrices only
    // after restoring the whole assembly, then reapply the current timeline position.
    reset(); root.updateMatrixWorld(true);
    if (primary && primary.frameCount > 1 && primary.framesPerSecond > 0 && Array.isArray(primary.tracks) && contextFor(primary)) {
      cache.set(packageName, primary); clip = primary;
      linkedClips = (result.companions || []).filter(value => value.targetPart !== primary.targetPart && contextFor(value));
      linkedWarnings = result.warnings || [];
      bundleCache.set(packageName, { companions:linkedClips, warnings:linkedWarnings });
      pose(time);
      last = performance.now();
      if (cache.size > 15) { const oldest = [...cache.keys()].find(key => !initialPackages.has(key) && key !== packageName);
        if (oldest) { cache.delete(oldest); bundleCache.delete(oldest); } }
    } else { if (!clip && !error) error = 'This animation has no matching visible rig in the viewer.'; }
    sync();
  }
  const clipResult = (packageName, result, failure) => bundleResult(packageName,
    result ? { primary:result, companions:[], warnings:[] } : null, failure);
  play.onclick = () => { if (!clip) return; if (!playing && time >= duration()) time = 0; playing = !playing; last = performance.now(); sync(); };
  restButton.onclick = () => { playing = false; pending = null; time = 0; reset(); restoreSeparate(); root.updateMatrixWorld(true); sync(); };
  scrub.oninput = () => { if (!clip) return; playing = false; time = Number(scrub.value) / 1000 * duration(); pose(time); sync(); };
  function update() {
    if (!playing || !clip) return;
    const now = performance.now(); time += (now - last) / 1000 * Number(speed.value); last = now;
    if (time >= duration()) {
      if (loop.checked) time %= duration(); else { time = duration(); playing = false; }
    }
    pose(time); sync();
  }
  function withRestPose() {
    const wasPlaying = playing, savedTime = time;
    playing = false; reset();
    root.updateMatrixWorld(true);
    return () => { if (clip && savedTime > 0) pose(savedTime); time = savedTime; playing = wasPlaying;
      last = performance.now(); sync(); };
  }
  sync();
  return { panel, update, rest: restButton.onclick, followBodyPose, withRestPose, bundleResult, clipResult };
};
