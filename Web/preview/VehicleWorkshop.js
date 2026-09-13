/* Batcomputer vehicle assembly workshop. Scene data is generated locally from verified cooked assets. */
(function () {
  'use strict';
  const data = window.VEHICLE_SCENE, $ = id => document.getElementById(id);
  const rad = Math.PI / 180, clone = x => JSON.parse(JSON.stringify(x));
  const editable = new Map(data.parts.filter(p => p.editable).map(p => [p.id, p]));
  let saved = new Map(data.saved.map(t => [t.component, clone(t)]));
  const materialKey = (id, slot) => id + ':' + slot;
  let materials = new Map((data.materialOverrides || []).map(m => [materialKey(m.component, m.slot), clone(m)])), disabled = new Set(data.disabledParts || []);
  let beamSettings = new Map((data.lights || []).map(l => [l.component, clone(l)]));
  let accentColor=data.accentColor?clone(data.accentColor):null, ghostBody=false;
  let lightSurfaces = new Map((data.lightSurfaces || []).map(s => [s.slot, clone(s)]));
  const lightRoles = data.lightRoles || [], surfaceChoices = data.lightSurfaceChoices || [];
  const surfaceRole = id => lightRoles.find(r => r.component === id && Array.from(lightSurfaces.values()).some(s => s.role === r.id));
  const catalog = data.materialCatalog || [], byPath = new Map(catalog.map(m => [m.path, m]));
  $('materialSource').add(new Option('Vehicle part materials', 'Parts'), 2);
  const copyMaterialButton = document.createElement('button'); copyMaterialButton.textContent = 'Copy / recolor…'; copyMaterialButton.id = 'copyMaterial'; copyMaterialButton.disabled = true; $('useMaterial').before(copyMaterialButton);
  copyMaterialButton.onclick = () => { if (!libraryChoice || !libraryTarget) return; $('materialPicker').close(); pushHost('vehicleWorkshopCopyMaterial', { ...draft(), copySource: libraryChoice.path, copyComponent: libraryTarget.id, copySlot: libraryTarget.slot }); };
  const initial = snapshot();
  const scene = new THREE.Scene(), camera = new THREE.PerspectiveCamera(38, 1, .01, 10000);
  camera.up.set(0, 0, 1);
  const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.75)); renderer.outputEncoding = THREE.sRGBEncoding;
  renderer.toneMapping = THREE.ACESFilmicToneMapping; renderer.toneMappingExposure = 1.05;
  $('viewport').appendChild(renderer.domElement);
  const orbit = new THREE.OrbitControls(camera, renderer.domElement); orbit.enableDamping = true;
  const hemi = new THREE.HemisphereLight(0xe6f0ff, 0x394052, 1.15); hemi.position.set(0, 0, 10); scene.add(hemi);
  for (const [pos, intensity] of [[[4, -3, 7], 1.7], [[-4, 5, 3], .85]]) { const light = new THREE.DirectionalLight(0xe7efff, intensity); light.position.fromArray(pos); scene.add(light); }
  const grid = new THREE.GridHelper(24, 48, 0x485260, 0x303744); grid.rotation.x = Math.PI / 2; grid.material.transparent = true; grid.material.opacity = .35; scene.add(grid);
  const vehicle = new THREE.Group(); scene.add(vehicle);
  const gizmo = new THREE.TransformControls(camera, renderer.domElement); gizmo.setSize(.85); scene.add(gizmo);
  const selectionBox = new THREE.Box3Helper(new THREE.Box3(), 0xffd43b); selectionBox.visible = false; scene.add(selectionBox);
  const loaded = new Map(), modelCache = new Map(), undo = [], redo = [], fields = new Map();
  let selected = null, selectedSlot = null, dragBefore = null, isolate = false, lights = true, mode = 'translate', ready = false, libraryPage = 0, libraryTarget = null, libraryChoice = null;
  const axisLegend = document.createElement('div'); axisLegend.className = 'axis-legend'; axisLegend.innerHTML = '<span class="ax">X · forward/back</span><span class="ay">Y · left/right</span><span class="az">Z · up/down</span>'; $('viewport').appendChild(axisLegend);
  const disableButton = document.createElement('button'); disableButton.id = 'disablePart'; disableButton.className = 'danger'; $('fields').before(disableButton);
  const weaponsButton = document.createElement('button'); weaponsButton.id = 'weaponsMode'; weaponsButton.textContent = 'Weapons'; $('seatsMode').before(weaponsButton);
  weaponsButton.onclick = () => { $('search').value = 'launcher'; $('filter').value = 'all'; select('socket:LauncherGadget_01'); };
  const boostButton = document.createElement('button'); boostButton.id = 'boostMode'; boostButton.textContent = 'Boost'; weaponsButton.after(boostButton);
  boostButton.onclick = () => { $('search').value = 'exhaust'; $('filter').value = 'all'; select('socket:VFX_Exhaust_01'); };
  const surfacePanel = document.createElement('fieldset'); surfacePanel.id = 'surfacePanel'; surfacePanel.hidden = true;
  surfacePanel.innerHTML = '<legend>Light surface · experimental</legend><select id="surfaceSlot" aria-label="Light surface slot" style="width:100%"></select><select id="surfaceRole" aria-label="Light behavior" style="width:100%;margin-top:6px"></select><p id="surfaceReason" class="muted"></p><button id="suggestLightRoles">Use named light slots</button>';
  $('fields').before(surfacePanel);
  $('surfaceSlot').onchange = () => { selectedSlot = Number($('surfaceSlot').value); updateInspector(); };
  function assignSurface(slot, role) {
    const choice = surfaceChoices.find(s => s.slot === slot); if (!choice?.canAssign && role) return;
    if (role && Array.from(lightSurfaces.values()).some(s => s.slot !== slot && s.role === role)) return;
    if (role) { const previous=lightSurfaces.get(slot); lightSurfaces.set(slot, {slot,role}); const host=lightRoles.find(r=>r.id===role); disabled.delete(host.component); materials.delete(materialKey(host.component,0)); if(previous?.role!==role)saved.delete(host.component); }
    else lightSurfaces.delete(slot);
  }
  $('surfaceRole').onchange = () => { const before=snapshot(); assignSurface(Number($('surfaceSlot').value),$('surfaceRole').value); syncLightSurfaces(); checkpoint(before); };
  $('suggestLightRoles').onclick = () => { const before=snapshot(); for(const s of surfaceChoices) if(s.canAssign && s.suggestedRole && !lightSurfaces.has(s.slot)) assignSurface(s.slot,s.suggestedRole); syncLightSurfaces(); checkpoint(before); };
  const riderButton = document.createElement('button'); riderButton.id='riders';riderButton.textContent='Seated figures';$('glow').after(riderButton);
  const ghostButton=document.createElement('button');ghostButton.id='ghostBody';ghostButton.textContent='Ghost body';riderButton.after(ghostButton);ghostButton.onclick=()=>{if(!ready)return;ghostBody=!ghostBody;ghostButton.classList.toggle('active',ghostBody);repaint(loaded.get('body'));};
  let ridersLoaded=false,ridersVisible=true,riderBusy=false;
  riderButton.onclick=()=>{if(!ready)return;if(!ridersLoaded){riderBusy=true;updateUi();riderButton.disabled=true;riderButton.textContent='Preparing figures…';pushHost('vehicleWorkshopRiders',{});}else{ridersVisible=!ridersVisible;loaded.forEach(s=>{if(s.rider)s.rider.visible=ridersVisible;});riderButton.classList.toggle('active',ridersVisible);}};
  const beamPanel = document.createElement('fieldset'); beamPanel.id = 'beamPanel'; beamPanel.hidden = true;
  beamPanel.innerHTML = '<legend>Light beam</legend><label>Color <input id="beamColor" type="color" aria-label="Beam color"></label><div class="triple"><label>Brightness<input id="beamIntensity" type="number" min="0" max="10000" step="any"></label><label>Range · cm<input id="beamRadius" type="number" min="1" max="10000" step="any"></label><label>Cone · °<input id="beamCone" type="number" min="1" max="89" step="any"></label></div><p id="beamState" class="muted"></p><button id="resetBeam">Use native light settings</button>';
  $('fields').before(beamPanel);
  const accentPanel=document.createElement('fieldset');accentPanel.id='accentPanel';accentPanel.hidden=true;accentPanel.innerHTML='<legend>Shared accent color</legend><input id="accentColor" type="color" aria-label="Accent light color"><button id="resetAccent">Use native color</button><p class="muted">All native accent meshes. Custom shaders must support vehicle light data.</p>';$('fields').before(accentPanel);
  $('accentColor').onchange=()=>{const before=snapshot(),c=new THREE.Color($('accentColor').value);accentColor={r:Math.round(c.r*255),g:Math.round(c.g*255),b:Math.round(c.b*255)};loaded.forEach(repaint);checkpoint(before);};
  $('resetAccent').onclick=()=>{const before=snapshot();accentColor=null;loaded.forEach(repaint);checkpoint(before);};
  for (const id of ['beamColor','beamIntensity','beamRadius','beamCone']) $(id).onchange = () => {
    if (!selected?.light) return; const color = new THREE.Color($('beamColor').value), value = { component:selected.id, r:Math.round(color.r*255), g:Math.round(color.g*255), b:Math.round(color.b*255), intensity:Number($('beamIntensity').value), radius:Number($('beamRadius').value), outerCone:Number($('beamCone').value) };
    if (![$('beamIntensity'),$('beamRadius'),$('beamCone')].every(e => e.value !== '' && e.checkValidity())) { updateInspector(); return; }
    const before = snapshot(); beamSettings.set(selected.id,value); paintBeam(loaded.get(selected.id)); checkpoint(before);
  };
  $('resetBeam').onclick = () => { if (!selected?.light) return; const before=snapshot();beamSettings.delete(selected.id);paintBeam(loaded.get(selected.id));checkpoint(before); };
  const fieldGroups = [['Position · cm', ['x', 'y', 'z'], ['X', 'Y', 'Z']], ['Rotation · degrees', ['pitch', 'yaw', 'roll'], ['Pitch', 'Yaw', 'Roll']], ['Scale', ['scaleX', 'scaleY', 'scaleZ'], ['X', 'Y', 'Z']]];
  for (const [label, names, captions] of fieldGroups) {
    const fs = document.createElement('fieldset'), legend = document.createElement('legend'), row = document.createElement('div'); legend.textContent = label; row.className = 'triple'; fs.append(legend, row);
    names.forEach((name, i) => { const label = document.createElement('label'), input = document.createElement('input'); label.textContent = captions[i]; input.type = 'number'; input.step = name.startsWith('scale') ? '.01' : '.1'; input.min = name.startsWith('scale') ? '.01' : names[0] === 'pitch' ? '-360' : '-10000'; input.max = name.startsWith('scale') ? '100' : names[0] === 'pitch' ? '360' : '10000'; input.className = ['x', 'y', 'z'][i]; input.setAttribute('aria-label', name); label.appendChild(input); row.appendChild(label); fields.set(name, input); input.onchange = () => numeric(name, input); });
    $('fields').appendChild(fs);
  }
  function ueQuaternion(t) {
    return new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 0, 1), t.yaw * rad)
      .multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), -t.pitch * rad))
      .multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -t.roll * rad));
  }
  function toNode(node, t) { node.position.set(t.x / 100, t.y / 100, t.z / 100); node.quaternion.copy(ueQuaternion(t)); node.scale.set(t.scaleX, t.scaleY, t.scaleZ); node.updateMatrixWorld(true); }
  function fromNode(id, node) {
    const e = new THREE.Euler().setFromQuaternion(node.quaternion, 'ZYX');
    const clean = v => Math.round(v * 1000) / 1000;
    return { component: id, x: clean(node.position.x * 100), y: clean(node.position.y * 100), z: clean(node.position.z * 100), pitch: clean(-e.y / rad), yaw: clean(e.z / rad), roll: clean(-e.x / rad), scaleX: clean(node.scale.x), scaleY: clean(node.scale.y), scaleZ: clean(node.scale.z) };
  }
  function valid(t) { return ['x', 'y', 'z'].every(k => Number.isFinite(t[k]) && Math.abs(t[k]) <= 10000) && ['pitch', 'yaw', 'roll'].every(k => Number.isFinite(t[k]) && Math.abs(t[k]) <= 360) && ['scaleX', 'scaleY', 'scaleZ'].every(k => Number.isFinite(t[k]) && t[k] >= .01 && t[k] <= 100); }
  function nativeTransform(part) { return surfaceRole(part.id)?{component:part.id,x:0,y:0,z:0,pitch:0,yaw:0,roll:0,scaleX:1,scaleY:1,scaleZ:1}:part.native; }
  function current(part) { return saved.get(part.id) || nativeTransform(part); }
  function draft() { return { transforms: Array.from(saved.values()), materialOverrides: Array.from(materials.values()), disabledParts: Array.from(disabled), lights:Array.from(beamSettings.values()), accentColor, lightSurfaces:Array.from(lightSurfaces.values()) }; }
  function snapshot() { return JSON.stringify(draft()); }
  function checkpoint(before) { if (before === snapshot()) return; undo.push(before); if (undo.length > 60) undo.shift(); redo.length = 0; updateUi(); }
  function restore(value) { const state = JSON.parse(value); saved = new Map(state.transforms.map(t => [t.component, t])); materials = new Map(state.materialOverrides.map(m => [materialKey(m.component, m.slot), m])); disabled = new Set(state.disabledParts); beamSettings = new Map((state.lights || []).map(l => [l.component,l]));accentColor=state.accentColor||null; lightSurfaces=new Map((state.lightSurfaces||[]).map(s=>[s.slot,s])); loaded.forEach(s => { toNode(s.node, current(s.part)); repaint(s); paintBeam(s); }); syncLightSurfaces(); visibility(); updateUi(); }
  function numeric(name, input) { if (!selected || !editable.has(selected.id)) return; const before = snapshot(), t = clone(current(selected)); t[name] = input.value === '' ? NaN : Number(input.value); if (!valid(t)) { $('status').textContent = 'Enter a valid value inside the shown limits.'; updateInspector(); return; } saved.set(selected.id, t); toNode(loaded.get(selected.id).node, t); checkpoint(before); }
  function pushHost(type, values) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(Object.assign({ type, vehicleId: data.vehicleId, session: data.session }, values)); }
  function changed(id) { return saved.has(id) || disabled.has(id) || beamSettings.has(id) || id==='body'&&lightSurfaces.size>0 || surfaceRole(id) || accentColor&&id.startsWith('C_') || Array.from(materials.values()).some(m => m.component === id); }
  function partLabel(part) { const role=surfaceRole(part.id);return role?role.label+' · custom lens':part.label; }
  function updateUi() { $('undo').disabled = !undo.length; $('redo').disabled = !redo.length; $('apply').disabled = !ready||riderBusy; $('setup').disabled=riderBusy;$('paintMode').disabled=riderBusy; $('counts').textContent = editable.size + ' adjustable · ' + materials.size + ' material edits'; $('status').textContent = riderBusy?'Preparing figures · seat edits can continue':snapshot() === initial ? 'Drag to orbit · Click a surface to select its material slot' : 'Unsaved changes · Save vehicle to keep them'; renderList(); updateInspector(); }
  function effectiveSlot(part, slot) { const override = materials.get(materialKey(part.id, slot.slot)); return override ? { ...slot, package: override.materialPath, color: null, family: byPath.get(override.materialPath)?.family || 'Assigned material', parameters: [] } : slot; }
  function materialList(target, slots) {
    target.replaceChildren();
    if (!slots.length) { const note = document.createElement('p'); note.className = 'muted'; note.textContent = 'No mesh material slots.'; target.appendChild(note); return; }
    slots.forEach(original => { const slot = target.id === 'materials' && selected ? effectiveSlot(selected, original) : original, item = document.createElement('div'); item.className = 'slot'; const title = document.createElement('strong'); title.textContent = 'Slot ' + slot.slot + ' · ' + (slot.color ? 'Flat color override' : slot.family); if (slot.color) { const swatch = document.createElement('span'); swatch.className = 'swatch'; swatch.style.background = '#' + new THREE.Color(...slot.color).convertLinearToSRGB().getHexString(); title.prepend(swatch); } item.appendChild(title); const path = document.createElement('div'); path.className = 'path'; path.textContent = slot.package; item.appendChild(path);
      if (target.id === 'materials') { const actions = document.createElement('div'); actions.className = 'actions'; const choose = document.createElement('button'); choose.textContent = selectedSlot === slot.slot ? 'Change selected surface…' : 'Choose material…'; choose.onclick = () => openMaterials(selected.id, slot.slot); choose.dataset.slot = slot.slot; const reset = document.createElement('button'); reset.textContent = 'Reset'; reset.disabled = !materials.has(materialKey(selected.id, slot.slot)); reset.onclick = () => { const before = snapshot(); materials.delete(materialKey(selected.id, slot.slot)); repaint(loaded.get(selected.id)); checkpoint(before); }; actions.append(choose, reset); item.appendChild(actions); if (selectedSlot === slot.slot) item.style.borderLeft = '3px solid #ffd43b'; }
      target.appendChild(item); });
  }
  function renderList() {
    const search = $('search').value.toLowerCase(), filter = $('filter').value; $('parts').replaceChildren();
    const groups = new Map(); for (const p of data.parts) { if (!(partLabel(p) + ' ' + p.label + ' ' + p.id + ' ' + p.socket).toLowerCase().includes(search) || filter === 'editable' && !p.editable || filter === 'changed' && !changed(p.id)) continue; if (!groups.has(p.group)) groups.set(p.group, []); groups.get(p.group).push(p); }
    groups.forEach((parts, group) => { const label = document.createElement('div'); label.className = 'group'; label.textContent = group.toUpperCase() + ' / ' + parts.length; $('parts').appendChild(label); for (const p of parts) { const button = document.createElement('button'); button.className = 'part' + (selected && selected.id === p.id ? ' active' : '') + (disabled.has(p.id) ? ' disabled' : ''); button.textContent = partLabel(p); button.dataset.part = p.id; if (changed(p.id)) { const tag = document.createElement('span'); tag.className = 'badge'; tag.textContent = disabled.has(p.id) ? 'REMOVED' : 'EDITED'; button.appendChild(tag); } const sub = document.createElement('small'); sub.textContent = surfaceRole(p.id)?'Custom surface · independent placement':p.socket || (p.id === 'body' ? 'Select surfaces to paint' : p.editable ? 'Component origin' : 'Reference'); button.appendChild(sub); button.onclick = () => select(p.id); $('parts').appendChild(button); } });
  }
  function updateInspector() {
    if (!selected) return; const t = current(selected), state = loaded.get(selected.id);
    $('selection').textContent = partLabel(selected); $('detail').textContent = surfaceRole(selected.id)?'Offset from the imported lens center · vehicle axes':(selected.socket ? 'Socket: ' + selected.socket + ' · Bone: ' + selected.bone : 'Vehicle origin') + (selected.id === 'body' ? ' · materials editable, rig locked' : selected.editable ? ' · editable' : ' · reference only');
    fields.forEach((input, name) => { input.value = Number(t[name]).toFixed(3); input.disabled = !selected.editable || (selected.group === 'Seating' || selected.lockScale) && name.startsWith('scale'); });
    disableButton.style.display = selected.canDisable && !surfaceRole(selected.id) ? '' : 'none'; disableButton.textContent = disabled.has(selected.id) ? 'Restore part in mod' : 'Remove part from mod';
    surfacePanel.hidden = selected.id !== 'body' || surfaceChoices.length === 0;
    if(!surfacePanel.hidden) {
      const slot = selectedSlot ?? surfaceChoices[0].slot, choice=surfaceChoices.find(s=>s.slot===slot), binding=lightSurfaces.get(slot);
      $('surfaceSlot').replaceChildren(...surfaceChoices.map(s=>new Option(s.name+' · slot '+s.slot,s.slot))); $('surfaceSlot').value=slot;
      $('surfaceRole').replaceChildren(new Option('Ordinary surface',''),...lightRoles.map(r=>{const o=new Option(r.label,r.id);o.disabled=Array.from(lightSurfaces.values()).some(s=>s.slot!==slot&&s.role===r.id);return o;}));$('surfaceRole').value=binding?.role||'';$('surfaceRole').disabled=!choice?.canAssign;
      $('surfaceReason').textContent=choice?.reason || (binding?'Uses a native lamp controller. Move its LED part in Assembly. Beam position is separate.':'Choose a lens-only slot. Assigning a role replaces that native LED and removes its old glow piece.');
    }
    beamPanel.hidden = !selected.light;
    accentPanel.hidden=selected.group!=='Accent lights';$('accentColor').value=accentColor?'#'+new THREE.Color(accentColor.r/255,accentColor.g/255,accentColor.b/255).getHexString():'#d5e9ec';$('resetAccent').disabled=!accentColor;
    if (selected.light) { const beam = beamSettings.get(selected.id) || selected.light; $('beamColor').value='#'+new THREE.Color(beam.r/255,beam.g/255,beam.b/255).getHexString();$('beamIntensity').value=beam.intensity;$('beamRadius').value=beam.radius;$('beamCone').value=beam.outerCone;$('resetBeam').disabled=!beamSettings.has(selected.id);$('beamState').textContent=beamSettings.has(selected.id)?'Custom beam · glow meshes use their own materials':'Native settings unchanged. Values shown are starting values for a custom beam.'; }
    $('fields').style.display = selected.editable ? '' : 'none';
    $('reset').parentElement.style.display = selected.editable ? 'flex' : 'none';
    $('reset').disabled = !selected.editable || !saved.has(selected.id); $('focus').disabled = !state;
    $('hide').textContent = state && !state.node.visible ? 'Show in preview' : 'Hide in preview';
    $('isolate').classList.toggle('active', isolate);
    $('partNote').textContent = selected.id === 'body' ? 'Click a surface to pick its shared material slot. All faces using that slot change together. Separate individual pieces into their own material slots in Blender.' : selected.note || 'Socket-local offsets. Removing a glow mesh does not remove its light beam. Light behaviour and brightness remain native.';
    if(surfaceRole(selected.id)) { $('materials').textContent='Custom light surface · uses the native bulb shader. Change its assignment on Vehicle body.'; }
    else materialList($('materials'), selected.materials);
  }
  function select(id, slot = null) { const state = loaded.get(id); selected = data.parts.find(p => p.id === id); selectedSlot = slot; if (!selected) return; gizmo.detach(); if (state && selected.editable && state.node.visible) gizmo.attach(state.node); $('scale').disabled = selected.group === 'Seating'||selected.lockScale; if ((selected.group === 'Seating'||selected.lockScale) && mode === 'scale') modeTo('translate'); if (isolate) visibility(); updateUi(); if (slot !== null && id==='body'&&surfaceChoices.length)surfacePanel.scrollIntoView({block:'nearest'});else if (slot !== null) $('materials').querySelector('[data-slot="' + slot + '"]')?.scrollIntoView({block:'nearest'});else $('inspector').scrollTop=0; }
  function bounds(id) { const state = id ? loaded.get(id) : null; return new THREE.Box3().setFromObject(state ? state.node : vehicle); }
  function frame(id, view) {
    const box = bounds(id); if (box.isEmpty()) return; const center = box.getCenter(new THREE.Vector3()), radius = Math.max(box.getSize(new THREE.Vector3()).length() / 2, .2);
    const direction = view === 'top' ? new THREE.Vector3(.001, 0, 1) : view === 'front' ? new THREE.Vector3(1, 0, .08) : view === 'side' ? new THREE.Vector3(0, -1, .08) : new THREE.Vector3(1, -1.2, .8).normalize();
    const fov = Math.min(camera.fov * rad, 2 * Math.atan(Math.tan(camera.fov * rad / 2) * camera.aspect));
    orbit.target.copy(center); camera.position.copy(center).addScaledVector(direction.normalize(), radius / Math.sin(fov / 2) * 1.12); camera.near = .01; camera.far = Math.max(1000, radius * 30); camera.updateProjectionMatrix(); orbit.update();
  }
  function visibility() { loaded.forEach((s, id) => s.node.visible = !s.hidden && !disabled.has(id) && !lightRoles.some(r=>r.glow===id&&Array.from(lightSurfaces.values()).some(b=>b.role===r.id)) && (!isolate || selected && id === selected.id || id === 'body')); if (selected && loaded.get(selected.id)?.node.visible && selected.editable) gizmo.attach(loaded.get(selected.id).node); else gizmo.detach(); updateInspector(); }
  function modeTo(value) { if (value === 'scale' && (selected?.group === 'Seating'||selected?.lockScale)) return; mode = value; gizmo.setMode(value); for (const id of ['translate', 'rotate', 'scale']) $(id).classList.toggle('active', id === value); }
  $('name').textContent = data.name; materialList($('nativeMaterials'), data.nativeMaterials);
  $('search').oninput = renderList; $('filter').onchange = renderList;
  for (const id of ['translate', 'rotate', 'scale']) $(id).onclick = () => modeTo(id);
  $('space').onchange = e => gizmo.setSpace(e.target.value);
  $('snap').onchange = () => { const value = Number($('snap').value); gizmo.setTranslationSnap(value ? value / 100 : null); gizmo.setRotationSnap(value ? ({ 1: 5, 5: 15, 10: 30 }[value] * rad) : null); gizmo.setScaleSnap(value ? .05 : null); };
  $('undo').onclick = () => { if (undo.length) { redo.push(snapshot()); restore(undo.pop()); } };
  $('redo').onclick = () => { if (redo.length) { undo.push(snapshot()); restore(redo.pop()); } };
  $('reset').onclick = () => { if (!selected || !selected.editable) return; const before = snapshot(); saved.delete(selected.id); toNode(loaded.get(selected.id).node, nativeTransform(selected)); checkpoint(before); };
  $('frame').onclick = () => frame(); $('focus').onclick = () => selected && frame(selected.id);
  document.querySelectorAll('[data-view]').forEach(button => button.onclick = () => frame(null, button.dataset.view));
  $('grid').onclick = () => { grid.visible = !grid.visible; $('grid').classList.toggle('active', grid.visible); };
  $('glow').onclick = () => { lights = !lights; vehicle.traverse(n => { if (n.isMesh && n.userData.glow) (Array.isArray(n.material) ? n.material : [n.material]).forEach(m => m.emissiveIntensity = lights ? 1.3 : 0); }); loaded.forEach(paintBeam); $('glow').classList.toggle('active', lights); };
  $('hide').onclick = () => { if (!selected) return; const state = loaded.get(selected.id); state.hidden = !state.hidden; visibility(); };
  $('showAll').onclick = () => { isolate = false; loaded.forEach(s => s.hidden = false); visibility(); };
  $('isolate').onclick = () => { isolate = !isolate; visibility(); };
  $('apply').onclick = () => pushHost('vehicleWorkshopSave', draft());
  const openSettings = () => { if (ready) pushHost('vehicleWorkshopSettings', draft()); }; $('setup').onclick = openSettings;
  $('assemblyMode').onclick = () => { $('search').value = ''; $('filter').value = 'all'; renderList(); };
  $('paintMode').onclick = () => { const part = selected?.materials.length ? selected : data.parts.find(p => p.id === 'body'); openMaterials(part.id, selectedSlot ?? part.materials[0]?.slot ?? 0); };
  $('seatsMode').onclick = () => { $('search').value = 'seat'; $('filter').value = 'all'; select('seat:SeatDriver'); };
  disableButton.onclick = () => { if (!selected?.canDisable) return; const before = snapshot(); if (disabled.has(selected.id)) disabled.delete(selected.id); else disabled.add(selected.id); visibility(); checkpoint(before); };
  gizmo.addEventListener('dragging-changed', event => orbit.enabled = !event.value);
  gizmo.addEventListener('mouseDown', () => { dragBefore = snapshot(); });
  gizmo.addEventListener('objectChange', () => { if (!selected || !selected.editable) return; const state = loaded.get(selected.id), value = fromNode(selected.id, state.node); if (valid(value)) { saved.set(selected.id, value); fields.forEach((input, name) => input.value = value[name].toFixed(3)); } else toNode(state.node, current(selected)); });
  gizmo.addEventListener('mouseUp', () => { if (dragBefore !== null) checkpoint(dragBefore); dragBefore = null; });
  const ray = new THREE.Raycaster(); let pointerDown = null;
  renderer.domElement.addEventListener('pointerdown', e => { pointerDown = [e.clientX, e.clientY]; });
  renderer.domElement.addEventListener('pointerup', e => {
    if (!pointerDown || Math.hypot(e.clientX - pointerDown[0], e.clientY - pointerDown[1]) > 4 || gizmo.axis || e.button !== 0) return;
    const rect = renderer.domElement.getBoundingClientRect(); ray.setFromCamera(new THREE.Vector2((e.clientX - rect.left) / rect.width * 2 - 1, -(e.clientY - rect.top) / rect.height * 2 + 1), camera);
    const hits = ray.intersectObject(vehicle, true); for (const hit of hits) { let n = hit.object; while (n && !n.userData.part) n = n.parent; if (n && loaded.get(n.userData.part).node.visible) { const slots = hit.object.userData.materialSlots; select(n.userData.part, slots?.[hit.face?.materialIndex || 0] ?? null); break; } }
  });
  addEventListener('keydown', e => { if (/INPUT|SELECT|TEXTAREA/.test(document.activeElement.tagName)) return; if (e.ctrlKey && ['z', 'y'].includes(e.key.toLowerCase())) { e.preventDefault(); $(e.key.toLowerCase() === 'z' ? 'undo' : 'redo').click(); } else if (!e.ctrlKey && !e.altKey) { if (e.key.toLowerCase() === 'w') modeTo('translate'); if (e.key.toLowerCase() === 'e') modeTo('rotate'); if (e.key.toLowerCase() === 'r') modeTo('scale'); if (e.key.toLowerCase() === 'f') frame(selected && selected.id); } });
  function appearance(node, part, parser) {
    let fallback = 0;
    node.traverse(n => { if (!n.isMesh) return; const colors = part.materials;
      n.userData.materialSlots = [];
      const one = original => { const primitive = parser.associations.get(n)?.primitives, association = parser.associations.get(original), index = primitive !== undefined && part.primitiveSlots?.[primitive] !== undefined ? part.primitiveSlots[primitive] : association && association.materials !== undefined ? association.materials : fallback++; n.userData.materialSlots.push(index); const slot = colors.find(s => s.slot === index) || colors[0]; const family = slot ? slot.family : '', glowing = part.group.includes('lights') && part.group !== 'Light sources (reference)';
        const color = slot && slot.color ? new THREE.Color(...slot.color) : new THREE.Color(glowing ? (part.group === 'Brake lights' || part.group === 'Rear lights' ? 0xf46c57 : part.group === 'Accent lights' ? 0x75cfee : 0xffedba) : family === 'Metal' ? 0x9ca7b5 : family === 'Rubber' ? 0x20242a : family === 'Transparent' ? 0x547887 : 0x4d5664);
        const m = new THREE.MeshStandardMaterial({ color, roughness: family === 'Rubber' ? .8 : .3, metalness: family === 'Metal' ? .75 : .05, skinning: !!n.isSkinnedMesh, side: THREE.DoubleSide, transparent: family === 'Transparent', opacity: family === 'Transparent' ? .42 : 1 });
        if (glowing) { m.emissive.copy(color); m.emissiveIntensity = 1.3; n.userData.glow = true; } return m; };
      n.material = Array.isArray(n.material) ? n.material.map(one) : one(n.material);
    });
  }
  function repaint(state) {
    state.node.traverse(n => { if (!n.isMesh || !n.userData.materialSlots) return;
      const list = Array.isArray(n.material) ? n.material : [n.material];
      if (!n.userData.baseMaterials) n.userData.baseMaterials = list.map(m => ({ color:m.color.clone(), emissive:m.emissive.clone(), intensity:m.emissiveIntensity, roughness:m.roughness, metalness:m.metalness, opacity:m.opacity, transparent:m.transparent }));
      list.forEach((m,i) => { const original=n.userData.baseMaterials[i], override=materials.get(materialKey(state.part.id,n.userData.materialSlots[i]));
        m.color.copy(original.color);m.emissive.copy(original.emissive);m.emissiveIntensity=original.intensity;m.roughness=original.roughness;m.metalness=original.metalness;m.opacity=original.opacity;m.transparent=original.transparent;
        if(override){const family=byPath.get(override.materialPath)?.family || 'Surface';m.color.setHex(family==='Metal'?0xaebaca:family==='Rubber'?0x20242a:family.startsWith('Glass')?0x6b92a5:0x8993a3);m.emissive.setHex(0);m.emissiveIntensity=0;m.metalness=family==='Metal'?.75:.05;m.roughness=family==='Rubber'?.8:.3;m.transparent=family.startsWith('Glass');m.opacity=m.transparent?.45:1;}
        if(state.part.group==='Accent lights'&&accentColor&&(!override||byPath.get(override.materialPath)?.family==='Vehicle glow'))m.emissive.setRGB(accentColor.r/255,accentColor.g/255,accentColor.b/255).convertSRGBToLinear();
        if(n.userData.glow)m.emissiveIntensity=lights?1.3:0;
        if(state.part.id==='body'&&ghostBody){m.transparent=true;m.opacity=.18;m.depthWrite=false;}else m.depthWrite=true;
        m.needsUpdate=true;
      });
    });
    if(state.surfaceProxy) state.surfaceProxy.traverse(n=>{if(n.isMesh){const c=state.part.group==='Accent lights'&&accentColor?new THREE.Color(accentColor.r/255,accentColor.g/255,accentColor.b/255).convertSRGBToLinear():new THREE.Color(state.part.group==='Brake lights'||state.part.group==='Rear lights'?0xff3322:0xa6f5ef);n.material.emissive.copy(c);n.material.emissiveIntensity=lights?1.3:0;}});
  }
  function syncLightSurfaces() {
    const body=loaded.get('body'); if(!body)return;
    // Restore before rebuilding. Proxies have independent geometry/materials; source GLTF stays intact.
    loaded.forEach(s=>{
      if(s.surfaceProxy){s.node.remove(s.surfaceProxy);s.surfaceProxy.traverse(n=>{if(n.isMesh){n.geometry.dispose();n.material.dispose();}});s.surfaceProxy=null;}
      s.anchor.position.fromArray(s.part.anchor.position).multiplyScalar(.01);s.anchor.quaternion.fromArray(s.part.anchor.quaternion);s.anchor.scale.fromArray(s.part.anchor.scale);
      toNode(s.node,current(s.part));
      if(s.baseChildren) s.baseChildren.forEach(c=>c.visible=true);
    });
    body.node.traverse(n=>{if(n.isMesh&&n.userData.materialSlots){n.visible=true;(Array.isArray(n.material)?n.material:[n.material]).forEach(m=>m.visible=true);}});
    vehicle.updateMatrixWorld(true);
    const inverse=new THREE.Matrix4().copy(vehicle.matrixWorld).invert();
    for(const binding of lightSurfaces.values()) {
      const role=lightRoles.find(r=>r.id===binding.role), choice=surfaceChoices.find(s=>s.slot===binding.slot), host=loaded.get(role?.component);if(!host||!choice)continue;
      host.baseChildren ??= Array.from(host.node.children);host.baseChildren.forEach(c=>c.visible=false);
      const proxy=new THREE.Group(), center=new THREE.Vector3(...choice.center).multiplyScalar(.01);
      body.node.traverse(n=>{
        if(!n.isMesh||!n.userData.materialSlots?.includes(binding.slot))return;
        const selectedGroups=n.userData.materialSlots.map((slot,i)=>slot===binding.slot?i:-1).filter(i=>i>=0);
        const geometry=n.geometry.clone();geometry.applyMatrix4(new THREE.Matrix4().multiplyMatrices(inverse,n.matrixWorld));geometry.translate(-center.x,-center.y,-center.z);
        if(n.userData.materialSlots.length>1){const groups=geometry.groups.filter(g=>selectedGroups.includes(g.materialIndex));geometry.clearGroups();groups.forEach(g=>geometry.addGroup(g.start,g.count,0));}else n.visible=false;
        (Array.isArray(n.material)?n.material:[n.material]).forEach((m,i)=>{if(selectedGroups.includes(i))m.visible=false;});
        const material=new THREE.MeshStandardMaterial({color:0xcccccc,emissive:0xffffff,emissiveIntensity:1.3,roughness:.3,side:THREE.DoubleSide});
        const lens=new THREE.Mesh(geometry,material);lens.userData.glow=true;proxy.add(lens);
      });
      host.anchor.position.copy(center);host.anchor.quaternion.identity();host.anchor.scale.set(1,1,1);host.node.add(proxy);host.surfaceProxy=proxy;repaint(host);
    }
    vehicle.updateMatrixWorld(true);
    visibility();
  }
  function openMaterials(id,slot) {
    if(riderBusy)return;
    const part=data.parts.find(p=>p.id===id);if(!part?.materials.some(m=>m.slot===slot))return;
    select(id,slot);libraryTarget={id,slot};libraryChoice=null;libraryPage=0;$('useMaterial').disabled=true;
    $('materialPickerTitle').textContent=part.label+' · slot '+slot;
    $('materialSource').value=part.id==='body'?'Vehicle':'Parts';$('materialSearch').value='';
    $('materialPath').textContent='Applies to every surface in this slot. Shader appearance here is approximate.';renderLibrary();$('materialPicker').showModal();$('materialSearch').focus();
  }
  function renderLibrary() {
    const search=$('materialSearch').value.toLowerCase().trim(),source=$('materialSource').value;
    const rows=catalog.filter(m=>(source==='All'||m.source===source||source==='Vehicle'&&m.vehicle||source==='Parts'&&(m.part||data.nativeMaterials.some(s=>s.package===m.path)))&&(!search||(m.name+' '+m.path).toLowerCase().includes(search)));
    const pages=Math.max(1,Math.ceil(rows.length/48));libraryPage=Math.min(libraryPage,pages-1);$('materialCards').replaceChildren();
    rows.slice(libraryPage*48,(libraryPage+1)*48).forEach(choice=>{const button=document.createElement('button');button.className='material-card'+(choice===libraryChoice?' active':'');const name=document.createElement('strong');name.textContent=choice.name;const detail=document.createElement('small');detail.textContent=choice.source+' · '+choice.family;button.append(name,detail);button.title=choice.path;button.onclick=()=>{libraryChoice=choice;$('materialPath').textContent=choice.path+(choice.family==='Vehicle glow'?' · A glow shader alone does not add brake/headlight behaviour.':'');$('useMaterial').disabled=false;renderLibrary();};$('materialCards').appendChild(button);});
    $('materialCount').textContent=rows.length+' materials · '+(libraryPage+1)+' / '+pages;$('materialPrevious').disabled=libraryPage===0;$('materialNext').disabled=libraryPage===pages-1; copyMaterialButton.disabled = !libraryChoice;
  }
  $('materialSearch').oninput=$('materialSource').onchange=()=>{libraryPage=0;libraryChoice=null;$('useMaterial').disabled=true;$('materialPath').textContent='Select a material. Preview shading is approximate.';renderLibrary();};
  $('materialPrevious').onclick=()=>{libraryPage--;renderLibrary();};$('materialNext').onclick=()=>{libraryPage++;renderLibrary();};$('closeMaterials').onclick=()=>$('materialPicker').close();
  $('useMaterial').onclick=()=>{if(!libraryTarget||!libraryChoice)return;const before=snapshot();materials.set(materialKey(libraryTarget.id,libraryTarget.slot),{component:libraryTarget.id,slot:libraryTarget.slot,materialPath:libraryChoice.path});repaint(loaded.get(libraryTarget.id));$('materialPicker').close();checkpoint(before);};
  async function load() {
    const loader = new THREE.GLTFLoader();
    for (const part of data.parts) {
      const anchor = new THREE.Group(); anchor.position.fromArray(part.anchor.position).multiplyScalar(.01); anchor.quaternion.fromArray(part.anchor.quaternion); anchor.scale.fromArray(part.anchor.scale); vehicle.add(anchor);
      const node = new THREE.Group(); node.userData.part = part.id; anchor.add(node); toNode(node, current(part));
      if (part.file) { if (!modelCache.has(part.file)) modelCache.set(part.file, await loader.loadAsync(part.file)); const gltf = modelCache.get(part.file), model = part.id === 'body' ? gltf.scene : gltf.scene.clone(true);
        // This CUE exporter writes positions as (UE.X, UE.Z, UE.Y), including the handedness
        // change. Swap Y/Z back; a rotation alone mirrors the vehicle and its attachments.
        model.rotation.x = Math.PI / 2; model.scale.z = -1; appearance(model, part, gltf.parser); node.add(model); }
      else { const marker = new THREE.Mesh(part.group === 'Seating' ? new THREE.BoxGeometry(.26,.32,.08) : new THREE.OctahedronGeometry(.045), new THREE.MeshBasicMaterial({ color: part.editable ? 0xffd43b : 0x71ccef, wireframe: true })); node.add(marker); if (part.group === 'Light sources (reference)' || part.group === 'Seating') { const arrow = new THREE.ArrowHelper(new THREE.Vector3(1, 0, 0), new THREE.Vector3(), .6, 0x71ccef, .12, .07); node.add(arrow); } }
      const state = { part, anchor, node, hidden: false }; loaded.set(part.id, state); repaint(state);
      if (part.light) { const beam = new THREE.SpotLight(0xffffff,1,24,Math.PI/3,.35,2);const target=new THREE.Object3D();target.position.set(1,0,0);node.add(target,beam);beam.target=target;state.beam=beam;const arrow=new THREE.ArrowHelper(new THREE.Vector3(1,0,0),new THREE.Vector3(),.6,0x71ccef,.12,.07);node.add(arrow);paintBeam(state); }
      if(part.group==='Weapons & grapple'||part.group==='Boost & exhaust')node.add(new THREE.ArrowHelper(new THREE.Vector3().fromArray(part.markerDirection||[1,0,0]).normalize(),new THREE.Vector3(),.8,part.group==='Boost & exhaust'?0x75dfff:0xffa84c,.15,.08));
    }
    ready = true; $('loading').style.display = 'none'; select('body'); syncLightSurfaces(); visibility(); frame();
    if (data.warnings.length) $('error').textContent = data.warnings.length + ' preview warnings · see part details';
    pushHost('vehicleWorkshopReady', { parts: loaded.size });
  }
  function paintBeam(state) { if (!state?.beam) return; const value=beamSettings.get(state.part.id)||state.part.light;state.beam.color.setRGB(value.r/255,value.g/255,value.b/255).convertSRGBToLinear();state.beam.intensity=lights?value.intensity/128:0;state.beam.distance=value.radius/100;state.beam.angle=value.outerCone*rad; }
  async function loadRiders(riders) {
    try {
      if (!riders) return;
      for(const rider of riders){const state=loaded.get(rider.seat);if(!state)throw new Error('Unknown seat');if(state.rider)continue;state.rider=await window.buildVehicleRider(rider);state.node.add(state.rider);}
      ridersLoaded=true;ridersVisible=true;riderButton.classList.add('active');$('status').textContent='Native seated idle poses · placement preview, not live driving IK';
    } catch(error){$('error').textContent='Seated preview: '+error.message;pushHost('vehicleWorkshopError',{error:error.message});}
    finally {riderBusy=false;riderButton.disabled=false;riderButton.textContent='Seated figures';updateUi();}
  }
  function resize() { const rect = $('viewport').getBoundingClientRect(); if (!rect.width || !rect.height) return; renderer.setSize(rect.width, rect.height); camera.aspect = rect.width / rect.height; camera.updateProjectionMatrix(); }
  new ResizeObserver(resize).observe($('viewport')); resize();
  function tick() { requestAnimationFrame(tick); orbit.update(); if (selected && loaded.has(selected.id)) { selectionBox.box.copy(bounds(selected.id)); selectionBox.visible = selected.editable && loaded.get(selected.id).node.visible; } renderer.render(scene, camera); } tick();
  load().catch(error => { $('loading').textContent = 'Preview could not be loaded: ' + error.message; $('error').textContent = 'Nothing was saved'; pushHost('vehicleWorkshopError', { error: error.message }); });
  // Deterministic hooks used by the local, headless interaction checks.
  window.vehicleWorkshop = { select, fromNode, ueQuaternion, valid, frame, camera, gizmo, openSettings, openMaterials, loadRiders, get ready() { return ready; }, get saved() { return clone(Array.from(saved.values())); }, get draft() { return clone(draft()); }, get loaded() { return loaded; }, get selectedSlot() { return selectedSlot; } };
})();
