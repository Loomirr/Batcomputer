/* Scene inspection only: no authoring transforms or visibility changes are persisted. */
window.BatcomputerCharacterWorkshop = function ({ THREE, scene, camera, controls, renderer, loaded, root, complete = true, onSurfaceSelected }) {
  const partNames = { CharacterMesh0: 'Body', __BareHead: 'Head base', Head: 'Head attachment', Face: 'Face', Torso: 'Chest attachment', Torso2: 'Second chest attachment', Cape: 'Cape', Collar: 'Collar' };
  const parts = loaded.map((entry, index) => ({ id: String(index), object: entry.scene,
    component: entry.m.part, nativeHidden: !!entry.m.hidden,
    label: entry.m.beside ? 'Glider · separate display' : entry.m.label || partNames[entry.m.part] || entry.m.part || entry.m.file }))
    .sort((a, b) => Number(a.nativeHidden) - Number(b.nativeHidden));
  let selected = null, helper = null, isolated = false, pointer = null;
  const visibility = new Map(parts.map(p => [p.object, p.object.visible]));
  const exportVisibility = new Map(visibility);
  const style = document.createElement('style');
  style.textContent = '#character-tools{position:absolute;left:12px;top:38px;width:220px;max-width:calc(100vw - 44px);padding:10px;background:#20252eee;border:1px solid #424955;border-radius:8px;color:#dfe4ea;font-size:12px}#character-tools select{width:100%;padding:6px;background:#171b22;color:#eee;border:1px solid #424955;margin:6px 0}#character-tools .buttons{display:flex;flex-wrap:wrap;gap:5px}#character-tools button{background:#303742;color:#eee;border:1px solid #535d6b;border-radius:4px;padding:5px 8px;cursor:pointer}#character-tools button:disabled{opacity:.45}#character-tools small{display:block;color:#aeb8c6;margin-top:7px}';
  document.head.appendChild(style);
  const panel = document.createElement('section'); panel.id = 'character-tools';
  const title = document.createElement('strong'); title.textContent = 'CHARACTER WORKSHOP'; panel.appendChild(title);
  const select = document.createElement('select'); select.setAttribute('aria-label', 'Character part');
  function option(value, text) { const item = document.createElement('option'); item.value = value; item.textContent = text; select.appendChild(item); }
  option('', 'Select a part…'); parts.forEach(p => option(p.id, p.label)); panel.appendChild(select);
  const buttons = document.createElement('div'); buttons.className = 'buttons'; panel.appendChild(buttons);
  function button(text, action) { const b = document.createElement('button'); b.type = 'button'; b.textContent = text; b.onclick = action; buttons.appendChild(b); return b; }
  const focusButton = button('Focus · F', () => focus(selected ? [selected] : parts));
  const isolateButton = button('Isolate', () => { if (!selected) return; isolated = !isolated; applyVisibility(); });
  const hideButton = button('Hide part', () => { if (!selected) return; visibility.set(selected.object, !visibility.get(selected.object)); applyVisibility(); });
  button('Show every part', () => { isolated = false; parts.forEach(p => visibility.set(p.object, true)); applyVisibility(); });
  button('Whole character', () => { isolated = false; applyVisibility(); focus(parts); });
  const hint = document.createElement('small'); hint.textContent = 'Hide/show and isolation are preview-only. Gliders are displayed beside the character.'; panel.appendChild(hint);
  const exportButton = button('Export GLB…', async () => {
    exportButton.disabled = true; hint.textContent = 'Preparing the whole character…';
    try {
      await window.BatcomputerCharacterExport.download(THREE, root || sceneRoot(), exportVisibility);
      hint.textContent = 'Choose where to save. Includes available rigs; game shaders and animations are not included.';
    } catch (error) { hint.textContent = 'Export failed: ' + error.message; }
    finally { exportButton.disabled = !complete; }
  });
  exportButton.disabled = !complete;
  exportButton.title = complete ? 'Export the whole assembly, even when isolated. Approximate preview materials; not a merged game-ready rig.' : 'A part failed to load. Reload the preview before exporting.';
  document.body.appendChild(panel);
  const layout = window.BatcomputerCharacterWorkshopShell({ THREE, scene, root: root || sceneRoot(), camera, controls, renderer, panel, parts,
    select: id => choose(parts.find(p => p.id === id) || null),
    focus: () => focus(selected ? [selected] : parts),
    whole: () => { isolated = false; applyVisibility(); focus(parts); } });
  layout.moveExport(exportButton);
  if (!complete) layout.reportError('Some parts failed to load; export is disabled');
  function applyVisibility() {
    parts.forEach(p => { p.object.visible = visibility.get(p.object) && (!isolated || p === selected); });
    isolateButton.textContent = isolated ? 'Show all' : 'Isolate';
    hideButton.textContent = selected && !visibility.get(selected.object) ? 'Show part' : 'Hide part';
    if (helper) helper.visible = !!selected?.object.visible;
  }
  function choose(part) {
    selected = part; select.value = part ? part.id : '';
    layout.select(part);
    focusButton.disabled = isolateButton.disabled = !part;
    hideButton.disabled = !part;
    if (helper) { scene.remove(helper); helper.geometry.dispose(); helper.material.dispose(); helper = null; }
    window.characterMeshEditor?.select(part?.component);
    if (!part) isolated = false;
    applyVisibility();
    if (part) {
      helper = new THREE.BoxHelper(part.object, 0xffd530); scene.add(helper);
      helper.visible = part.object.visible;
      // Choosing a surface also selects its existing placement controls, without saving edits.
      ['partuv', 'meshmove'].forEach(id => {
        const picker = document.querySelector('#' + id + ' select');
        if (picker && [...picker.options].some(o => o.value === part.component)) {
          picker.value = part.component; picker.dispatchEvent(new Event('change'));
        }
      });
    }
  }
  function focus(targets) {
    scene.updateMatrixWorld(true);
    const box = window.BatcomputerVisibleBounds ? window.BatcomputerVisibleBounds(THREE, targets.map(p => p.object)) : new THREE.Box3();
    if (!window.BatcomputerVisibleBounds) targets.forEach(p => box.union(new THREE.Box3().setFromObject(p.object)));
    if (box.isEmpty()) return;
    const center = box.getCenter(new THREE.Vector3()), size = box.getSize(new THREE.Vector3());
    const radius = Math.max(size.length() / 2, 0.01);
    const halfFov = Math.atan(Math.tan(camera.fov * Math.PI / 360) * Math.min(1, camera.aspect));
    const distance = radius / Math.sin(halfFov) * 1.2;
    const direction = camera.position.clone().sub(controls.target).normalize();
    if (!direction.lengthSq()) direction.set(1, 0.2, 0).normalize();
    camera.position.copy(center).addScaledVector(direction, distance);
    // Keep far clipping large enough for the full character after focusing a tiny detail.
    const full = new THREE.Box3().setFromObject(sceneRoot());
    camera.near = Math.max(radius / 1000, 0.00001);
    camera.far = Math.max(distance * 10, full.getSize(new THREE.Vector3()).length() * 40, 100);
    camera.updateProjectionMatrix(); controls.target.copy(center); controls.update();
  }
  function sceneRoot() { return parts[0]?.object.parent || scene; }
  select.onchange = () => choose(parts.find(p => p.id === select.value) || null);
  const raycaster = new THREE.Raycaster();
  function hitMaterial(hit) { return Array.isArray(hit.object.material) ? hit.object.material[hit.face?.materialIndex ?? 0] : hit.object.material; }
  function visible(hit) {
    for (let node = hit.object; node; node = node.parent) if (!node.visible) return false;
    return !hitMaterial(hit) || hitMaterial(hit).visible !== false;
  }
  renderer.domElement.tabIndex = 0;
  renderer.domElement.setAttribute('aria-label', 'Character 3D viewport');
  renderer.domElement.addEventListener('pointerdown', e => {
    // A canvas is not focusable by default. Give viewport interaction keyboard focus so a
    // previously edited numeric field cannot continue swallowing W/E/R or F.
    renderer.domElement.focus({ preventScroll: true });
    pointer = e.button === 0 ? { x: e.clientX, y: e.clientY } : null;
  });
  renderer.domElement.addEventListener('pointermove', e => { if (pointer && Math.hypot(e.clientX - pointer.x, e.clientY - pointer.y) > 4) pointer.dragged = true; });
  renderer.domElement.addEventListener('pointercancel', () => { pointer = null; });
  renderer.domElement.addEventListener('pointerup', e => {
    const start = pointer; pointer = null;
    if (window.characterMeshEditor?.interacting || window.characterMeshEditor?.gizmo.axis) return;
    if (!start || start.dragged || Math.hypot(e.clientX - start.x, e.clientY - start.y) > 4) return;
    const rect = renderer.domElement.getBoundingClientRect();
    raycaster.setFromCamera(new THREE.Vector2((e.clientX - rect.left) / rect.width * 2 - 1, 1 - (e.clientY - rect.top) / rect.height * 2), camera);
    const hit = raycaster.intersectObjects(parts.filter(p => p.object.visible).map(p => p.object), true).find(visible);
    let part = null;
    if (hit) for (let node = hit.object; node && !part; node = node.parent) part = parts.find(p => p.object === node) || null;
    choose(part);
    if (hit && onSurfaceSelected) onSurfaceSelected(hitMaterial(hit));
  });
  const keydown = e => {
    if (e.ctrlKey || e.altKey || e.metaKey || /^(INPUT|SELECT|TEXTAREA)$/.test(e.target.tagName) || e.target.isContentEditable) return;
    if (e.key.toLowerCase() === 'f' && selected) { e.preventDefault(); focus([selected]); }
    if (e.key === 'Escape') choose(null);
  };
  window.addEventListener('keydown', keydown);
  choose(null); focus(parts);
  return { select: id => choose(parts.find(p => p.id === id) || null), focus: () => focus(selected ? [selected] : parts),
    selectComponent: component => choose(parts.find(p => p.component === component) || null),
    resize: layout.resize, reportError: layout.reportError,
    update: () => { if (helper) helper.update(); } };
};
