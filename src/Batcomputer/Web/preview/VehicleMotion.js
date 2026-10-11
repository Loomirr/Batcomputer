/* Motion preview for the vehicle workshop: native mechanics clips plus procedural wheel, steering and suspension tests.
   Poses the preview skeleton only. Nothing here is saved or changes how the game animates the vehicle. */
window.createVehicleMotion = function (ctx) {
  'use strict';
  const { vehicle, loaded, bodyModel } = ctx, V3 = THREE.Vector3, Q = THREE.Quaternion, M4 = THREE.Matrix4, rad = Math.PI / 180;
  // glTF model space: X forward, Y up, Z right (UE X, Z, Y). The part node maps it back onto the vehicle axes.
  const FORWARD = new V3(1, 0, 0), UP = new V3(0, 1, 0), LATERAL = new V3(0, 0, 1);
  const bones = new Map(); bodyModel.traverse(n => { if (n.isBone && !bones.has(n.name)) bones.set(n.name, n); });
  const rest = new Map(); bones.forEach((b, name) => rest.set(name, { p: b.position.clone(), q: b.quaternion.clone(), s: b.scale.clone() }));
  const local = t => new M4().compose(new V3(t[0], t[1], t[2]), new Q(t[3], t[4], t[5], t[6]).normalize(), new V3(t[7] ?? 1, t[8] ?? 1, t[9] ?? 1));
  // Evaluate on the donor rig and apply model-space deltas. This also works with corrected GLBs,
  // without changing authored rest poses or storing animation frames in the project.
  const rig = (ctx.data.rig || []).map(b => ({ ...b, reference: local(b.reference) })), index = new Map(rig.map((b, i) => [b.name, i]));
  const rigRest = []; rig.forEach((b, i) => rigRest[i] = b.parent >= 0 ? new M4().multiplyMatrices(rigRest[b.parent], b.reference) : b.reference.clone());
  const rigRestInverse = rigRest.map(m => m.clone().invert());
  bodyModel.updateMatrixWorld(true);
  const modelInverse = new M4().copy(bodyModel.matrixWorld).invert(), inModel = node => new M4().multiplyMatrices(modelInverse, node.matrixWorld);
  const previewRest = new Map(); bones.forEach((b, name) => previewRest.set(name, inModel(b)));
  const posedBones = Array.from(bones.entries()).filter(([name]) => index.has(name)).sort((a,b) => index.get(a[0])-index.get(b[0]));
  const has = (...names) => names.every(n => bones.has(n) && index.has(n));
  const wheels = ['Wheel_FL', 'Wheel_FR', 'Wheel_BL', 'Wheel_BR'].filter(n => has(n));
  const corners = ['ChassisAttach_FL', 'ChassisAttach_FR', 'ChassisAttach_BL', 'ChassisAttach_BR'].filter(n => has(n));
  // The wheel bone sits at the axle and the rig root on the ground, so the axle height approximates the tyre radius.
  const wheelRadius = wheels.length ? Math.max(.08, Math.min(1, new V3().setFromMatrixPosition(rigRest[index.get(wheels[0])]).y)) : .3;

  // Parts on a moving bone follow it: anchor = (bone now · bone rest⁻¹) · anchor rest, all in vehicle space.
  const followers = [];
  vehicle.updateMatrixWorld(true);
  const boneInVehicle = bone => new M4().copy(vehicle.matrixWorld).invert().multiply(bone.matrixWorld);
  loaded.forEach(state => { const bone = bones.get(state.part.bone); if (!bone || state.part.id === 'body') return;
    state.anchor.updateMatrix(); followers.push({ state, bone, anchorRest: state.anchor.matrix.clone(), boneRestInverse: boneInVehicle(bone).invert() }); });

  const tests = [];
  if (wheels.length) tests.push({ id: 'test:spin', label: 'Wheel spin', duration: 4, note: 'Wheels roll forward at a steady speed. Checks wheel pivots and that tyres stay round on their axles.' });
  if (has('Wheel_FL', 'Wheel_FR')) tests.push({ id: 'test:steer', label: 'Steering', duration: 4, note: 'Front wheels steer left and right' + (has('SteeringWheel') ? ' with the steering wheel.' : '.') + ' Checks arch clearance at full lock.' });
  if (corners.length) tests.push({ id: 'test:suspension', label: 'Suspension', duration: 3, note: 'Each corner compresses and extends in turn. Checks tyre clearance inside the arches.' + (bones.has('Main_Suspension_L_F') ? ' Suspension arms are driven by the game’s animation blueprint and stay at rest here.' : '') });
  if (tests.length > 1) tests.push({ id: 'test:drive', label: 'Drive test (all)', duration: 6, note: 'Spin, steering and suspension together. An approximation of driving, not the game’s vehicle physics.' });
  const clips = (ctx.data.animations || []).filter(c => rig.length && c.tracks.some(t => has(t.bone))).map(c => ({ ...c, id: 'native:' + c.id, duration: Math.max(c.frameCount - 1, 1) / c.framesPerSecond, note: 'Native animation ' + c.package.split('/').pop() + '. Parts attached to ' + c.tracks.map(t => t.bone).filter(b => has(b)).join(', ') + ' follow it.' }));
  const all = [...clips, ...tests];

  const style = document.createElement('style');
  style.textContent = '#motionPanel{position:absolute;right:12px;bottom:58px;width:min(340px,calc(100% - 24px));background:#242a33f2;border:1px solid #39414f;border-radius:10px;padding:10px;z-index:2}#motionPanel .row{display:flex;gap:6px;align-items:center;margin-bottom:8px}#motionPanel select{flex:1;min-width:0}#motionPanel input[type=range]{flex:1;min-width:0;padding:0}#motionPanel .time{font-size:11px;color:#a4b0c2;min-width:74px;text-align:right}#motionPanel p{margin:0;font-size:11px;color:#a4b0c2;line-height:1.4}#motionPanel label{font-size:11px;color:#a4b0c2;display:flex;gap:4px;align-items:center}';
  document.head.appendChild(style);
  const panel = document.createElement('div'); panel.id = 'motionPanel'; panel.hidden = true;
  panel.innerHTML = '<div class="row"><select id="motionClip" aria-label="Animation"></select><button id="motionPlay">Play</button><button id="motionRest" title="Return to the rest pose">Rest</button></div>' +
    '<div class="row"><input id="motionScrub" type="range" min="0" max="1000" value="0" aria-label="Animation time"><span class="time" id="motionTime">0.00 s</span></div>' +
    '<div class="row"><label><input id="motionLoop" type="checkbox" checked> Loop</label><label>Speed <select id="motionSpeed" aria-label="Playback speed"><option value=".25">0.25×</option><option value=".5">0.5×</option><option value="1" selected>1×</option><option value="2">2×</option></select></label></div><p id="motionNote"></p>';
  ctx.viewport.appendChild(panel);
  const $ = id => document.getElementById(id), select = $('motionClip');
  const group = (label, list) => { if (!list.length) return; const g = document.createElement('optgroup'); g.label = label; list.forEach(c => g.appendChild(new Option(c.label, c.id))); select.appendChild(g); };
  group('Native animations', clips); group('Tests', tests);
  const button = document.createElement('button'); button.id = 'motion'; button.textContent = 'Motion'; button.disabled = !all.length;
  button.title = all.length ? 'Preview native animations and wheel, steering and suspension tests' : 'This rig has no previewable motion';
  ctx.viewbar.appendChild(button);

  let clip = all[0] || null, time = 0, playing = false, last = 0, posed = false;
  const current = () => all.find(c => c.id === select.value) || null;
  function note() { $('motionNote').textContent = clip ? clip.note : ''; }
  function show(open) { panel.hidden = !open; button.classList.toggle('active', open); if (!open) { playing = false; toRest(); } syncUi(); }
  function syncUi() { $('motionPlay').textContent = playing ? 'Pause' : 'Play'; if (clip) { $('motionScrub').value = Math.round(time / clip.duration * 1000); $('motionTime').textContent = time.toFixed(2) + ' / ' + clip.duration.toFixed(2) + ' s'; } }
  button.onclick = () => show(panel.hidden);
  select.onchange = () => { clip = current(); time = 0; toRest(); if (clip) pose(0); note(); syncUi(); };
  $('motionPlay').onclick = () => { if (!clip) return; if (!playing && time >= clip.duration) time = 0; playing = !playing; last = performance.now(); syncUi(); };
  $('motionRest').onclick = () => { playing = false; time = 0; toRest(); syncUi(); };
  $('motionScrub').oninput = () => { if (!clip) return; playing = false; time = $('motionScrub').value / 1000 * clip.duration; pose(time); syncUi(); };
  if (clip) select.value = clip.id; note();

  function toRest() {
    bones.forEach((b, name) => { const r = rest.get(name); b.position.copy(r.p); b.quaternion.copy(r.q); b.scale.copy(r.s); });
    followers.forEach(f => f.anchorRest.decompose(f.state.anchor.position, f.state.anchor.quaternion, f.state.anchor.scale));
    posed = false;
  }
  const spinAbout = (world, axis, angle) => { if (!angle) return; const p = new V3().setFromMatrixPosition(world); world.premultiply(new M4().makeTranslation(-p.x, -p.y, -p.z)).premultiply(new M4().makeRotationAxis(axis, angle)).premultiply(new M4().makeTranslation(p.x, p.y, p.z)); };
  function sampleNative(c, t, locals) {
    const f = Math.min(t * c.framesPerSecond, c.frameCount - 1), i = Math.floor(f), j = Math.min(i + 1, c.frameCount - 1), k = f - i;
    for (const track of c.tracks) {
      if (!index.has(track.bone)) continue;
      const a = track.frames[i], b = track.frames[j], q = new Q(a[3], a[4], a[5], a[6]).slerp(new Q(b[3], b[4], b[5], b[6]), k), mix = n => a[n] + (b[n] - a[n]) * k;
      locals[index.get(track.bone)] = local([mix(0), mix(1), mix(2), q.x, q.y, q.z, q.w, mix(7), mix(8), mix(9)]);
    }
  }
  function testExtras(id, t, duration) {
    const extras = new Map(), add = (name, fn) => { if (has(name)) extras.set(index.get(name), fn); };
    const spin = id === 'test:spin' || id === 'test:drive', steer = id === 'test:steer' || id === 'test:drive', bounce = id === 'test:suspension' || id === 'test:drive';
    const phase = t / duration * Math.PI * 2, lock = steer ? 28 * rad * Math.sin(phase) : 0;
    // Forward roll: the top of the tyre moves toward +X, a negative turn about the lateral axis. Steering then yaws the wheel.
    for (const name of wheels) add(name, world => { if (spin) spinAbout(world, LATERAL, -t * (5 / wheelRadius)); if (name.startsWith('Wheel_F')) spinAbout(world, UP, lock); });
    add('SteeringWheel', world => spinAbout(world, FORWARD, -lock * 3.2));
    if (bounce) corners.forEach((name, i) => add(name, world => world.premultiply(new M4().makeTranslation(0, .05 * Math.sin(phase * (id === 'test:drive' ? 2 : 1) + i * Math.PI / 2), 0))));
    return extras;
  }
  function pose(t) {
    toRest(); if (!clip || !rig.length) return;
    const locals = rig.map(b => b.reference), extras = clip.id.startsWith('native:') ? new Map() : testExtras(clip.id, t, clip.duration);
    if (clip.id.startsWith('native:')) sampleNative(clip, t, locals);
    const worlds = [];
    rig.forEach((b, i) => { worlds[i] = b.parent >= 0 ? new M4().multiplyMatrices(worlds[b.parent], locals[i]) : locals[i].clone(); extras.get(i)?.(worlds[i]); });
    // Preview bones are parent-first, so each parent's posed model matrix is known before its children.
    const posedModel = new Map();
    for (const [name, bone] of posedBones) {
      const i = index.get(name), target = new M4().multiplyMatrices(worlds[i], rigRestInverse[i]).multiply(previewRest.get(name));
      posedModel.set(bone, target);
      const parent = posedModel.get(bone.parent) || inModel(bone.parent);
      new M4().copy(parent).invert().multiply(target).decompose(bone.position, bone.quaternion, bone.scale);
    }
    bodyModel.updateMatrixWorld(true);
    followers.forEach(f => new M4().multiplyMatrices(boneInVehicle(f.bone), f.boneRestInverse).multiply(f.anchorRest).decompose(f.state.anchor.position, f.state.anchor.quaternion, f.state.anchor.scale));
    posed = true;
  }
  function update() {
    if (!playing || !clip || panel.hidden) return;
    const now = performance.now(); time += (now - last) / 1000 * Number($('motionSpeed').value); last = now;
    if (time >= clip.duration) { if ($('motionLoop').checked) time %= clip.duration; else { time = clip.duration; playing = false; } }
    pose(time); syncUi();
  }
  return { update, clips: all, get posed() { return posed; }, play(id) { select.value = id; select.onchange(); show(true); playing = true; last = performance.now(); syncUi(); }, pose(id, t) { select.value = id; select.onchange(); show(true); time = Math.max(0, Math.min(clip?.duration || 0, t)); pose(time); syncUi(); }, rest: () => $('motionRest').onclick(), reanchor() { followers.forEach(f => { f.state.anchor.updateMatrix(); f.anchorRest.copy(f.state.anchor.matrix); }); } };
};
