// Custom static-mesh authoring only. Never attach a gizmo to a native component.
window.BatcomputerMeshMath = function (THREE) {
  const basis = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -Math.PI / 2);
  const clone = t => ({ scale: t.scale, offset: [...t.offset], rotation: [...t.rotation] });
  const valid = t => t && Number.isFinite(t.scale) && t.scale >= 1 && t.scale <= 1000 &&
    t.offset?.length === 3 && t.rotation?.length === 3 && t.offset.every(v => Number.isFinite(v) && Math.abs(v) <= 100000) &&
    t.rotation.every(v => Number.isFinite(v) && Math.abs(v) <= 360);
  function position(a) { return new THREE.Vector3(a[0], a[2], -a[1]).multiplyScalar(.01); }
  function rotation(a) {
    const r = Math.PI / 180;
    const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(a[2] * r, a[0] * r, a[1] * r, 'ZYX'));
    return basis.clone().multiply(q).multiply(basis.clone().invert());
  }
  function toNode(node, t) { node.position.copy(position(t.offset)); node.quaternion.copy(rotation(t.rotation)); node.scale.setScalar(t.scale / 100); }
  function fromNode(node) {
    const q = basis.clone().invert().multiply(node.quaternion).multiply(basis);
    const e = new THREE.Euler().setFromQuaternion(q, 'ZYX'), deg = 180 / Math.PI;
    return { scale: node.scale.x * 100, offset: [node.position.x * 100, -node.position.z * 100, node.position.y * 100], rotation: [e.y * deg, e.z * deg, e.x * deg] };
  }
  function apply(state, t) {
    if (!valid(t)) return false;
    const saved = state.authored, delta = rotation(t.rotation).multiply(rotation(saved.rotation).invert());
    const old = position(saved.offset), next = position(t.offset), ratio = t.scale / saved.scale, v = new THREE.Vector3();
    for (const entry of state.customGeometry || []) {
      const geometry = entry.mesh.geometry, p = geometry.attributes.position, n = geometry.attributes.normal;
      for (let i = 0; i < entry.position.length; i += 3) {
        v.fromArray(entry.position, i).sub(old).multiplyScalar(ratio).applyQuaternion(delta).add(next); p.setXYZ(i / 3, v.x, v.y, v.z);
      }
      p.needsUpdate = true;
      if (n && entry.normal) {
        for (let i = 0; i < entry.normal.length; i += 3) { v.fromArray(entry.normal, i).applyQuaternion(delta).normalize(); n.setXYZ(i / 3, v.x, v.y, v.z); }
        n.needsUpdate = true;
      }
      const tangent = geometry.attributes.tangent;
      if (tangent && entry.tangent) {
        for (let i = 0; i < entry.tangent.length; i += 4) {
          v.fromArray(entry.tangent, i).applyQuaternion(delta).normalize();
          tangent.setXYZW(i / 4, v.x, v.y, v.z, entry.tangent[i + 3]);
        }
        tangent.needsUpdate = true;
      }
      geometry.computeBoundingBox(); geometry.computeBoundingSphere();
    }
    state.liveTransform = clone(t); return true;
  }
  return { clone, valid, position, rotation, toNode, fromNode, apply };
};

window.BatcomputerCharacterMeshEditor = function ({ THREE, scene, root, camera, renderer, controls, states, post, layout, canSave, onSelect }) {
  const math = window.BatcomputerMeshMath(THREE), parts = [...states.values()].filter(s => s.custom && s.customId && s.authored && s.customGeometry);
  if (!parts.length) return null;
  const el = (tag, parent, text) => { const n = document.createElement(tag); if (text) n.textContent = text; parent?.appendChild(n); return n; };
  const panel = el('div', document.body); panel.id = 'meshmove';
  el('label', panel, 'Custom mesh'); const picker = el('select', panel); picker.setAttribute('aria-label', 'Editable custom part');
  parts.forEach(s => { const o = el('option', picker, s.label || s.component); o.value = s.component; });
  const tools = el('div', panel); tools.className = 'cw-mesh-actions';
  const button = (text, fn, parent = tools) => { const b = el('button', parent, text); b.type = 'button'; b.onclick = fn; return b; };
  const anchor = new THREE.Group(), proxy = new THREE.Object3D(); anchor.matrixAutoUpdate = false; anchor.add(proxy); scene.add(anchor);
  const origin = new THREE.AxesHelper(.08); anchor.add(origin); origin.visible = false;
  const gizmo = new THREE.TransformControls(camera, renderer.domElement); gizmo.setSize(.85); scene.add(gizmo);
  // Only proportional scale handles; the cooked authoring format has one scalar, not XYZ scale.
  for (const group of [gizmo.children[0].gizmo.scale, gizmo.children[0].picker.scale])
    [...group.children].filter(h => ['X', 'Y', 'Z'].includes(h.name)).forEach(h => group.remove(h));
  let selected = null, mode = 'translate', beforeDrag = null, interacting = false, bakeQueued = false, baking = false, bakeTarget = null;
  const undo = [], redo = [], pending = new Map(), timers = new Map(), ghosted = new Map(), errors = new Set();
  // Authored data also carries import IDs; compare transform values only.
  const same = (a, b) => Math.abs(a.scale - b.scale) < 1e-5 && a.offset.every((v, i) => Math.abs(v - b.offset[i]) < 1e-5) &&
    a.rotation.every((v, i) => Math.abs(v - b.rotation[i]) < 1e-5), current = s => s.liveTransform || s.authored;
  const modes = new Map();
  function setMode(value) { mode = value; gizmo.setMode(value); modes.forEach((b, key) => b.classList.toggle('active', key === value)); }
  [['translate', 'Move · W'], ['rotate', 'Rotate · E'], ['scale', 'Scale · R']].forEach(([value, title]) => modes.set(value, button(title, () => setMode(value))));
  const space = el('select', panel); space.setAttribute('aria-label', 'Transform coordinate space');
  [['local', 'Local axes'], ['world', 'World axes']].forEach(([value, title]) => { const o = el('option', space, title); o.value = value; });
  space.onchange = () => gizmo.setSpace(space.value); space.value = 'local'; space.onchange();
  function snap(title, choices, apply) {
    const row = el('label', panel, title), select = el('select', row); select.setAttribute('aria-label', title);
    choices.forEach(([value, title]) => { const o = el('option', select, title); o.value = value; });
    select.onchange = () => apply(Number(select.value) || null); return select;
  }
  snap('Move snap', [[0, 'Off'], [1, '1 cm'], [5, '5 cm'], [10, '10 cm']], v => gizmo.setTranslationSnap(v ? v / 100 : null));
  snap('Rotate snap', [[0, 'Off'], [5, '5°'], [15, '15°'], [45, '45°']], v => gizmo.setRotationSnap(v ? v * Math.PI / 180 : null));
  snap('Scale snap', [[0, 'Off'], [1, '1 unit'], [5, '5 units'], [10, '10 units']], v => gizmo.setScaleSnap(v ? v / 100 : null));
  el('p', panel, 'World arrows: red X, green up, blue depth. Local axes follow the part. Fields use socket-local Unreal XYZ (cm). Scale is uniform.').className = 'cw-muted';
  const inputs = {};
  [['scale', 'Scale', 1, 1000], ['x', 'X (cm)', -100000, 100000], ['y', 'Y (cm)', -100000, 100000], ['z', 'Z (cm)', -100000, 100000],
    ['pitch', 'Pitch (°)', -360, 360], ['yaw', 'Yaw (°)', -360, 360], ['roll', 'Roll (°)', -360, 360]].forEach(([key, title, min, max]) => {
    const row = el('label', panel, title); row.className = 'axis'; const input = el('input', row); input.type = 'number'; input.step = '.1'; input.min = min; input.max = max; input.dataset.key = key; input.setAttribute('aria-label', title); inputs[key] = input;
    input.onchange = () => {
      if (!selected || baking || bakeQueued) { syncFields(); return; }
      const n = k => inputs[k].value.trim() === '' ? NaN : Number(inputs[k].value);
      const t = { scale: n('scale'), offset: [n('x'), n('y'), n('z')], rotation: [n('pitch'), n('yaw'), n('roll')] };
      if (!math.valid(t)) { status.textContent = 'Invalid transform. Scale: 1–1000; finite offsets and rotations required.'; syncFields(); return; }
      change(selected, t);
    };
  });
  const history = el('div', panel); history.className = 'cw-mesh-actions';
  const undoButton = button('Undo', () => step(undo, redo, 'before'), history), redoButton = button('Redo', () => step(redo, undo, 'after'), history);
  button('Reset to loaded', () => selected && change(selected, selected.authored), history);
  button('Turn 180°', () => { if (!selected) return; const t = math.clone(current(selected)); t.rotation[1] = (t.rotation[1] + 540) % 360 - 180; change(selected, t); }, history);
  const ghostLabel = el('label', panel, 'Ghost surrounding parts'), ghost = el('input', ghostLabel); ghost.type = 'checkbox'; ghost.setAttribute('aria-label', 'Ghost surrounding parts'); ghost.onchange = refreshGhost;
  const originLabel = el('label', panel, 'Show attachment origin'), originToggle = el('input', originLabel); originToggle.type = 'checkbox'; originToggle.checked = true; originToggle.setAttribute('aria-label', 'Show attachment origin');
  const status = el('p', panel); status.setAttribute('role', 'status'); status.className = 'cw-note';
  const retry = button('Retry draft save', () => { for (const id of [...errors]) postDraft(states.get(id)); }, panel);
  retry.hidden = true;
  const bake = button('Bake to game', () => {
    if (!selected || baking || bakeQueued) return;
    flush(); if (errors.size) { status.textContent = 'A draft save failed. Edit/retry that part before baking.'; return; }
    bakeTarget = selected; bakeQueued = true; gizmo.detach(); finishBake(); updateStatus();
  }, panel); bake.className = 'save';
  el('small', panel, canSave ? 'Draft edits auto-save to the project. Bake rebuilds the game mesh; rebuild the mod afterward. Reset/undo also update the draft.' : 'Preview only. Open a saved suit project to bake changes.');

  function postDraft(s) { const t = math.clone(current(s)); pending.set(s.component, t); errors.delete(s.component); post({ type: 'save-custom-mesh-draft', layout, component: s.component, customId: s.customId, transform: t }); updateStatus(); }
  function schedule(s) { if (!canSave) return; clearTimeout(timers.get(s.component)); timers.set(s.component, setTimeout(() => { timers.delete(s.component); postDraft(s); }, 400)); }
  function flush() { for (const [id, timer] of timers) { clearTimeout(timer); postDraft(states.get(id)); } timers.clear(); }
  function finishBake() { if (!bakeQueued || pending.size || timers.size || errors.size) return; bakeQueued = false; baking = true; gizmo.detach(); post({ type: 'save-custom-mesh', layout, component: bakeTarget.component, customId: bakeTarget.customId, transform: math.clone(current(bakeTarget)) }); bakeTarget = null; }
  function updateStatus() {
    const dirty = parts.filter(s => !same(current(s), s.authored)).length;
    status.textContent = baking ? 'Baking… the viewer reloads after success.' : bakeQueued ? 'Waiting for draft saves…' : errors.size ? 'Draft save failed. Your preview edits are still here; retry before baking.' : dirty ? `${dirty} part(s) have unbaked changes${pending.size || timers.size ? ' · saving draft…' : ''}` : 'No new placement changes.';
    status.classList.toggle('cw-warning', !!dirty || !!errors.size); bake.disabled = !canSave || !selected || baking || bakeQueued;
    retry.hidden = !errors.size; retry.disabled = baking;
    undoButton.disabled = !undo.length || baking || bakeQueued; redoButton.disabled = !redo.length || baking || bakeQueued;
  }
  function syncFields() { if (!selected) return; const t = current(selected), values = [t.scale, ...t.offset, ...t.rotation]; Object.values(inputs).forEach((input, i) => input.value = Number(values[i].toFixed(4))); }
  function refreshProxy() { if (!selected) return; selected.scene.updateWorldMatrix(true, false); anchor.matrix.copy(selected.scene.matrixWorld); math.toNode(proxy, current(selected)); anchor.updateMatrixWorld(true); }
  function commit(s, t) { math.apply(s, t); refreshProxy(); syncFields(); schedule(s); updateStatus(); }
  function checkpoint(s, before) { const after = math.clone(current(s)); if (same(before, after)) return; undo.push({ id: s.component, before: math.clone(before), after }); if (undo.length > 60) undo.shift(); redo.length = 0; }
  function change(s, t) { if (baking || bakeQueued || !math.valid(t)) return; const before = math.clone(current(s)); commit(s, t); checkpoint(s, before); updateStatus(); }
  function step(from, to, key) { if (baking || bakeQueued || !from.length) return; const item = from.pop(); to.push(item); onSelect(item.id); select(item.id); commit(states.get(item.id), item[key]); }
  function restoreGhost() { ghosted.forEach((saved, m) => { Object.assign(m, saved); delete m.userData.previewGhostOriginal; m.needsUpdate = true; }); ghosted.clear(); }
  function refreshGhost() {
    restoreGhost(); if (!ghost.checked || !selected) return;
    const keep = new Set(); selected.scene.traverse(n => { if (n.isMesh) (Array.isArray(n.material) ? n.material : [n.material]).forEach(m => keep.add(m)); });
    root.traverse(n => { if (!n.isMesh) return; for (const m of Array.isArray(n.material) ? n.material : [n.material]) {
      if (keep.has(m) || ghosted.has(m)) continue;
      const saved = { opacity: m.opacity, transparent: m.transparent, depthWrite: m.depthWrite }; ghosted.set(m, saved); m.userData.previewGhostOriginal = saved;
      m.opacity = Math.min(m.opacity, .18); m.transparent = true; m.depthWrite = false; m.needsUpdate = true;
    } });
  }
  function select(id) {
    if (interacting || baking) return;
    selected = parts.find(s => s.component === id) || null; gizmo.detach(); origin.visible = false;
    if (selected) { picker.value = id; refreshProxy(); if (selected.scene.visible && !bakeQueued) gizmo.attach(proxy); syncFields(); }
    refreshGhost(); updateStatus();
  }
  picker.onchange = () => { if (selected?.component !== picker.value) onSelect(picker.value); select(picker.value); };
  gizmo.addEventListener('dragging-changed', e => { interacting = e.value; controls.enabled = !e.value; });
  gizmo.addEventListener('mouseDown', () => { if (selected) beforeDrag = math.clone(current(selected)); });
  gizmo.addEventListener('objectChange', () => {
    if (!selected || baking || bakeQueued) return;
    if (mode === 'scale') {
      const axis = gizmo.axis || ''; const key = axis.endsWith('Y') ? 'y' : axis.endsWith('Z') ? 'z' : 'x';
      proxy.scale.setScalar(Math.max(.01, Math.min(10, proxy.scale[key])));
    }
    const t = math.fromNode(proxy); if (math.valid(t)) { math.apply(selected, t); syncFields(); updateStatus(); } else refreshProxy();
  });
  gizmo.addEventListener('mouseUp', () => { if (selected && beforeDrag) { checkpoint(selected, beforeDrag); schedule(selected); } beforeDrag = null; updateStatus(); });
  const keydown = e => {
    // Part and tool buttons keep keyboard focus after a click; they must not suppress shortcuts.
    // Text/numeric entry and native select type-ahead still own their keys.
    if (/^(INPUT|SELECT|TEXTAREA)$/.test(e.target.tagName) || e.target.isContentEditable || !selected || baking || bakeQueued || interacting) return;
    if ((e.ctrlKey || e.metaKey) && ['z', 'y'].includes(e.key.toLowerCase())) { e.preventDefault(); e.key.toLowerCase() === 'y' || e.shiftKey ? step(redo, undo, 'after') : step(undo, redo, 'before'); }
    else if (!e.ctrlKey && !e.altKey && !e.metaKey && { w: 'translate', e: 'rotate', r: 'scale' }[e.key.toLowerCase()]) { e.preventDefault(); setMode({ w: 'translate', e: 'rotate', r: 'scale' }[e.key.toLowerCase()]); }
  };
  window.addEventListener('keydown', keydown);
  window.addEventListener('pagehide', () => { flush(); restoreGhost(); gizmo.dispose(); origin.geometry.dispose(); origin.material.dispose(); window.removeEventListener('keydown', keydown); }, { once: true });
  setMode('translate'); select(null);
  return { select, gizmo, get interacting() { return interacting; },
    draftResult(component, transform, error) {
      const expected = pending.get(component); if (!expected || !math.valid(transform)) return;
      const sent = [expected.scale, ...expected.offset, ...expected.rotation], received = [transform.scale, ...transform.offset, ...transform.rotation];
      // The native host stores Single values; allow its rounding at large valid offsets.
      if (sent.some((v, i) => Math.abs(v - received[i]) > Math.max(.001, Math.abs(v) * 1e-6))) return;
      pending.delete(component); if (error) { errors.add(component); bakeQueued = false; } finishBake(); updateStatus();
    },
    bakeFailed(error) { baking = bakeQueued = false; select(selected?.component); status.textContent = 'Bake failed: ' + error; },
    update() { if (!selected) return; if (!interacting) refreshProxy(); const visible = selected.scene.visible && !baking && !bakeQueued; origin.visible = visible && originToggle.checked; if (!visible) gizmo.detach(); else if (!gizmo.object) gizmo.attach(proxy); }
  };
};
