// Layout/presentation only. Existing controls retain their own save and bake behavior.
window.BatcomputerCharacterWorkshopShell = function ({ THREE, scene, root, camera, controls, renderer, panel, parts, select, focus, whole }) {
  const el = (tag, parent, text, id) => { const e = document.createElement(tag); if (text) e.textContent = text; if (id) e.id = id; if (parent) parent.appendChild(e); return e; };
  function button(parent, text, action, title) { const b = el('button', parent, text); b.type = 'button'; b.onclick = action; if (title) b.title = title; return b; }
  const shell = el('section', document.body, null, 'character-workshop');
  const header = el('header', shell), brand = el('div', header);
  el('small', brand, 'BATCOMPUTER'); el('strong', brand, 'Character workshop');
  const actions = el('div', header); actions.className = 'cw-actions';
  button(actions, 'Parts', () => { shell.classList.toggle('cw-hide-parts'); resize(); });
  const inspectorButton = button(actions, 'Inspector', () => setInspector(shell.classList.contains('cw-hide-inspector')));
  button(actions, 'Clean view', () => { shell.classList.toggle('cw-clean'); resize(); });
  const main = el('main', shell);
  const sidebar = el('aside', main, null, 'cw-parts');
  const partHeading = el('div', sidebar); partHeading.className = 'cw-section-title';
  el('strong', partHeading, 'ASSEMBLY'); el('span', partHeading, String(parts.length));
  const search = el('input', sidebar); search.type = 'search'; search.placeholder = 'Find a part…'; search.setAttribute('aria-label', 'Find a character part');
  const list = el('div', sidebar, null, 'cw-part-list');
  const rows = new Map();
  parts.forEach(part => {
    const row = button(list, '', () => select(part.id)); row.className = 'cw-part'; row.setAttribute('aria-pressed', 'false');
    el('strong', row, part.label); el('small', row, (part.component || 'Native mesh') + (part.nativeHidden ? ' · hidden by game' : '')); rows.set(part.id, row);
  });
  const empty = el('p', list, 'No matching parts.'); empty.hidden = true;
  search.oninput = () => {
    let count = 0; const query = search.value.trim().toLowerCase();
    parts.forEach(p => { const show = (p.label + ' ' + p.component).toLowerCase().includes(query); rows.get(p.id).hidden = !show; if (show) count++; });
    empty.hidden = count > 0;
  };
  sidebar.appendChild(panel);
  const viewport = el('div', main, null, 'cw-viewport'); viewport.appendChild(renderer.domElement);
  const views = el('div', viewport); views.className = 'cw-views';
  for (const [label, direction] of [['Front', [1, 0, 0]], ['Back', [-1, 0, 0]], ['Side', [0, 0, 1]], ['Top', [.0001, 1, 0]], ['¾', [1, .25, 1]]])
    button(views, label, () => { camera.position.copy(controls.target).add(new THREE.Vector3(...direction)); focus(); }, 'Frame ' + label.toLowerCase() + ' view');
  button(views, 'Fit', whole);
  el('small', viewport, 'Drag to orbit · right-drag to pan · scroll to zoom').className = 'cw-orbit-hint';
  const inspector = el('aside', main, null, 'cw-inspector');
  const heading = el('h2', inspector, 'Scene');
  const detail = el('p', inspector, 'Select a part to inspect it.'); detail.className = 'cw-muted';
  const tabs = el('nav', inspector); tabs.className = 'cw-tabs'; tabs.setAttribute('aria-label', 'Inspector sections');
  const host = el('div', inspector, null, 'cw-panel-host');
  const sections = new Map(), tabButtons = new Map();
  for (const [id, title, ids, description] of [
    ['placement', 'Placement', ['meshmove'], 'Native parts use game attachment placement. Only imported custom parts can be moved; Bake to game and rebuild to apply those edits.'],
    ['materials', 'Surfaces', ['partuv', 'matedit'], 'Inspect maps, UV sets and face layers. These controls do not edit saved materials.'],
    ['face', 'Face', ['exprwrap'], 'Preview facial expressions; does not change game animations.'],
    ['scene', 'Scene', ['redbrick'], 'Lighting and Red Brick previews do not change your mod.']]) {
    tabButtons.set(id, button(tabs, title, () => setTab(id)));
    const section = el('section', host); sections.set(id, section); el('p', section, description).className = 'cw-note';
    let found = false;
    for (const panelId of ids) { const control = document.getElementById(panelId); if (control) { control.hidden = false; section.appendChild(control); found = true; } }
    if (!found && id !== 'scene') el('p', section, 'No controls available for this model.').className = 'cw-muted';
  }
  const placementEmpty = el('p', sections.get('placement'), 'Native placement is read-only. Use Hide part to inspect overlapping meshes.'); placementEmpty.className = 'cw-muted'; placementEmpty.hidden = true;
  const scenePanel = sections.get('scene');
  const lights = []; scene.traverse(node => { if (node.isLight) lights.push([node, node.intensity]); });
  const lightingLabel = el('label', scenePanel, 'Lighting');
  const lighting = el('select', lightingLabel); lighting.setAttribute('aria-label', 'Preview lighting');
  for (const name of ['Studio', 'Soft', 'Contrast']) { const option = el('option', lighting, name); option.value = name; }
  lighting.onchange = () => lights.forEach(([light, native]) => { light.intensity = native * (lighting.value === 'Soft' ? (light.isHemisphereLight ? 1.1 : .55) : lighting.value === 'Contrast' ? (light.isHemisphereLight ? .45 : .9) : 1); });
  const exposureLabel = el('label', scenePanel, 'Exposure'), exposure = el('input', exposureLabel);
  exposure.type = 'range'; exposure.min = '.5'; exposure.max = '1.8'; exposure.step = '.05'; exposure.value = String(renderer.toneMappingExposure || 1.1);
  exposure.setAttribute('aria-label', 'Preview exposure'); exposure.oninput = () => { renderer.toneMappingExposure = Number(exposure.value); };
  const bounds = window.BatcomputerVisibleBounds ? window.BatcomputerVisibleBounds(THREE, [root]) : new THREE.Box3().setFromObject(root), extent = Math.max(bounds.getSize(new THREE.Vector3()).length(), .1);
  const grid = new THREE.GridHelper(extent * 2, 24, 0x536071, 0x35404d); grid.position.y = (bounds.isEmpty() ? 0 : bounds.min.y) - extent * .003;
  grid.material.transparent = true; grid.material.opacity = .3; scene.add(grid);
  const gridLabel = el('label', scenePanel, 'Floor grid'), gridToggle = el('input', gridLabel);
  gridToggle.type = 'checkbox'; gridToggle.checked = true; gridToggle.onchange = () => { grid.visible = gridToggle.checked; };
  button(scenePanel, 'Reset lighting', () => { lighting.value = 'Studio'; lighting.onchange(); exposure.value = '1.1'; exposure.oninput(); });
  const footer = el('footer', shell);
  const status = el('span', footer, 'Preview ready · select a part to inspect it.');
  const diagnosticsButton = button(footer, 'Diagnostics', () => { shell.classList.toggle('cw-debug'); diagnosticsButton.setAttribute('aria-expanded', String(shell.classList.contains('cw-debug'))); });
  const diagnostics = document.getElementById('err'); if (diagnostics) shell.appendChild(diagnostics);
  document.body.classList.add('cw-ready');
  function setTab(id) {
    sections.forEach((section, name) => { section.hidden = name !== id; });
    tabButtons.forEach((button, name) => { button.classList.toggle('active', name === id); button.setAttribute('aria-pressed', String(name === id)); });
  }
  function setInspector(show) { shell.classList.toggle('cw-hide-inspector', !show); resize(); }
  function resize() {
    const rect = viewport.getBoundingClientRect(); if (!rect.width || !rect.height) return;
    renderer.setSize(rect.width, rect.height); camera.aspect = rect.width / rect.height; camera.updateProjectionMatrix();
    inspectorButton.setAttribute('aria-expanded', String(!shell.classList.contains('cw-hide-inspector') && !shell.classList.contains('cw-clean')));
  }
  const observer = new ResizeObserver(resize); observer.observe(viewport);
  window.addEventListener('pagehide', () => { observer.disconnect(); grid.geometry.dispose(); grid.material.dispose(); }, { once: true });
  if (window.innerWidth < 700) shell.classList.add('cw-hide-inspector');
  setTab('placement'); resize();
  return { resize, setInspector,
    reportError(message) { status.textContent = 'Preview issue: ' + message + ' · see Diagnostics'; status.classList.add('cw-warning'); },
    select(part) {
      rows.forEach((row, id) => { row.classList.toggle('active', id === part?.id); row.setAttribute('aria-pressed', String(id === part?.id)); });
      heading.textContent = part?.label || 'Scene'; detail.textContent = part ? (part.component || 'Native part') : 'Select a part to inspect it.';
      let available = false;
      for (const id of ['meshmove']) {
        const control = document.getElementById(id), picker = control?.querySelector('select');
        const show = !part || (picker && [...picker.options].some(option => option.value === part.component));
        if (control) { control.hidden = !show; if (show) available = true; }
      }
      placementEmpty.hidden = available || !part;
    },
    moveExport(button) { actions.appendChild(button); button.className = 'cw-primary'; }
  };
};
