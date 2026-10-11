// Keyed native icon-template cameras are converted from Blender (X,Y,Z) to glTF (X,Z,-Y).
// This is a viewer-material approximation, not an Unreal or Eevee shader reproduction.
window.BatcomputerCharacterIconStudio = function ({ THREE, root, loaded, complete, withNeutralFace, canTestSuit = false, layoutKey = '', post }) {
  // Native left/right are destination slots, not the direction the face points in the PNG.
  // The template has four camera keyframes, not four objects. Use every keyed transform,
  // including the torso camera's shorter distance and slight downward pitch.
  const measured = (label, size, frame, position, back) => {
    const direction = new THREE.Vector3(...back).normalize();
    return {label,size,frame,lens:89.67384338378906,position,back:direction.toArray(),
      yaw:THREE.MathUtils.radToDeg(Math.atan2(direction.y,direction.x)),
      pitch:THREE.MathUtils.radToDeg(Math.asin(direction.z)),
      distance:Math.hypot(position[0],position[1])*.01/Math.hypot(direction.x,direction.y)};
  };
  const presets = {
    menu: measured('Menu portrait',512,3,[249.055755615,0.000006280,145.477584839],[1,0,0]),
    left: measured('Left slot',512,2,[234.269195557,-88.891006470,145.477584839],[.934957504,-.354759812,.000000030]),
    right: measured('Right slot',512,4,[236.246292114,83.494705200,145.477584839],[.942848027,.333223462,.000000015]),
    suit: measured('Suit tile',256,1,[214.912338257,-.000003722,84.802169800],[.999997616,.000000060,.002191782])
  };
  // Numerical light settings only; no meshes, textures or artwork from the reference .blend.
  // Each rectangular emitter includes its world-space scaled axes and dimensions.
  const lights = [
    { p: [10.6613,61.5294,114.3043], power:25.8, color:[1,1,1] },
    { p: [-104.7084,-166.5668,49.1603], power:2881.9, rim:'cool', color:[.170365,.478724,1], area:[585.40855,144.62861], back:[-.210364,-.976752,.041271], up:[.973513,-.205426,.100360] },
    { p: [68.4114,-45.5352,191.2195], power:1225.2, color:[.224711,.235116,.226706] },
    { p: [164.699,-211.593,187.1911], power:10, color:[.030036,.030036,.030036], area:[929.2,929.2], back:[.73779,-.51027,.44216], up:[.558,.829,.0254] },
    { p: [-223.376877,344.1017,58.3337], suitP: [-124.384033203,344.1017,58.3337], power:779.5, rim:'warm', color:[.992956,.774374,0], area:[585.40855,144.62861], back:[-.210363,.976752,.041271], up:[-.973514,-.205425,-.100359] }
  ];
  const convert = a => new THREE.Vector3(a[0], a[2], -a[1]);
  let renderer = null, environment = null, dialog = null, currentCanvas = null, lightsInitialized = false, testPending = false, applyPending = false;
  const visibility = new Map(loaded.map(entry => [entry.scene, !entry.m.hidden && !entry.m.beside]));
  const defaults = role => ({zoom:1,lift:0,pan:0,yaw:0,pitch:0,lens:presets[role].lens});
  const framing = Object.fromEntries(Object.keys(presets).map(role => [role,defaults(role)]));
  const lightDefaults = {exposure:0,warm:1,cool:1,key:1};
  const lighting = {...lightDefaults};
  const iconExposure={value:Math.pow(2,-.56644344)};
  let selected = 'menu', pending = 0, studioSnapshot = null;
  function capture() {
    const restoreFace = withNeutralFace?.();
    try { return prepareSnapshot(window.BatcomputerCharacterExport.snapshot(THREE,root,visibility)); }
    finally { restoreFace?.(); }
  }
  function freeSnapshot(snapshot) {
    snapshot?.iconMaterials?.forEach(material=>material.dispose());
    snapshot?.root.traverse(node => { if (node.isSkinnedMesh) node.skeleton.dispose(); });
    snapshot?.dispose();
  }
  function prepareSnapshot(snapshot) {
    // Keep the main viewer's shaders, cache keys and material state untouched.
    const copies=new Map();
    const materialCopy=source=>{
      if(!source)return source;if(copies.has(source))return copies.get(source);
      const material=source.clone(),previous=source.onBeforeCompile,key=source.customProgramCacheKey();
      material.onBeforeCompile=function(shader,renderer){
        previous.call(this,shader,renderer);shader.uniforms.bcIconExposure=iconExposure;
        // Own implementation of the Khronos PBR Neutral equations used by the template.
        // https://github.com/KhronosGroup/ToneMapping/tree/main/PBR_Neutral
        shader.fragmentShader=`uniform float bcIconExposure;
vec3 bcIconNeutral(vec3 value) {
  value=max(value,vec3(0.0));
  float minimum=min(min(value.x,value.y),value.z);
  value-=minimum<0.08 ? minimum*(1.0-6.25*minimum) : 0.04;
  float maximum=max(max(value.x,value.y),value.z);
  if(maximum<=0.76)return value;
  float compressed=1.0-0.0576/(maximum-0.52);
  float retained=1.0/(1.0+0.15*(maximum-compressed));
  return value*(compressed/maximum)*retained+vec3(compressed)*(1.0-retained);
}\n`+shader.fragmentShader;
        shader.fragmentShader=shader.fragmentShader.replace('#include <tonemapping_fragment>','gl_FragColor.rgb=bcIconNeutral(gl_FragColor.rgb*bcIconExposure);');
        shader.fragmentShader=shader.fragmentShader.replace('#include <encodings_fragment>','#include <encodings_fragment>\ngl_FragColor.rgb=pow(clamp(gl_FragColor.rgb,0.0,1.0),vec3(1.0/1.258847117));');
      };
      material.customProgramCacheKey=()=>key+'|icon-pbr-neutral-v1';material.needsUpdate=true;copies.set(source,material);return material;
    };
    snapshot.root.traverse(node=>{if(node.isMesh)node.material=Array.isArray(node.material)?node.material.map(materialCopy):materialCopy(node.material);});
    snapshot.iconMaterials=[...copies.values()];return snapshot;
  }

  function render({ preset = 'menu', zoom = 1, lift = 0, pan = 0, yaw = 0, pitch = 0, lens = null, exposure = 0, warm = 1, cool = 1, key = 1, neutralFace = true, captured = null } = {}) {
    if (!complete) throw new Error('Some character parts failed to load. Reload the viewer before rendering icons.');
    if (!presets[preset]) throw new Error('Unknown icon layout.');
    lens ??= presets[preset].lens;
    if (![zoom,lift,exposure].every(Number.isFinite) || zoom < .5 || zoom > 1.5 || Math.abs(lift) > .35 || Math.abs(exposure) > 2)
      throw new Error('Icon framing values are outside the supported range.');
    if (![warm,cool,key].every(value => Number.isFinite(value) && value >= 0 && value <= 3)) throw new Error('Unsupported lighting intensity.');
    if (![pan,yaw,pitch,lens].every(Number.isFinite) || Math.abs(pan)>.4 || Math.abs(yaw)>55 || Math.abs(pitch)>30 || lens<50 || lens>150)
      throw new Error('Icon camera values are outside the supported range.');
    // The preview expression is a diagnostic control. Icons always capture the middle frame of
    // the game's neutral clip, then put the viewer back exactly where the user left it.
    const restoreFace = !captured && neutralFace ? withNeutralFace?.() : null;
    let snapshot;
    try {
      snapshot = captured || prepareSnapshot(window.BatcomputerCharacterExport.snapshot(THREE, root, visibility));
      if (!renderer) {
        renderer = new THREE.WebGLRenderer({ antialias:true, alpha:true, preserveDrawingBuffer:true });
        // PMREM render-target textures belong to their WebGL context. Build this one locally;
        // borrowing the main viewer's texture silently produces black metallic surfaces.
        const sky = document.createElement('canvas'); sky.width = 8; sky.height = 8;
        const context = sky.getContext('2d');
        // Measured world: linear .05 background × 3.1 strength, not the main viewer's gradient.
        context.fillStyle = '#282828'; context.fillRect(0,0,8,8);
        const texture = new THREE.CanvasTexture(sky); texture.mapping = THREE.EquirectangularReflectionMapping;
        const pmrem = new THREE.PMREMGenerator(renderer); environment = pmrem.fromEquirectangular(texture);
        pmrem.dispose(); texture.dispose();
      }
      const scene = new THREE.Scene(); scene.add(snapshot.root);
      scene.environment = environment.texture;
      const sun = new THREE.DirectionalLight(0xffffff, key); sun.position.set(0,3,0); scene.add(sun);
      if (!lightsInitialized) { THREE.RectAreaLightUniformsLib.init(); lightsInitialized = true; }
      for (const item of lights) {
        const color = new THREE.Color(...item.color);
        // Keep the measured light geometry/colors, but use one WebGL total-power budget.
        // Mixing Cycles' unnormalized warm radiance with normalized blue flux made the
        // warm light thousands of times stronger. All areas now use their metre area.
        const power = item.power * .01 * (item.rim === 'warm' ? warm : item.rim === 'cool' ? cool : key);
        const light = item.area
          ? new THREE.RectAreaLight(color, power / (Math.PI * item.area[0]*item.area[1]*.0001), item.area[0] * .01, item.area[1] * .01)
          : new THREE.PointLight(color, power / (4 * Math.PI), 0, 2);
        light.position.copy(convert(preset==='suit' && item.suitP ? item.suitP : item.p).multiplyScalar(.01));
        if (item.area) {
          light.up.copy(convert(item.up).normalize());
          light.lookAt(light.position.clone().sub(convert(item.back)));
        }
        scene.add(light);
      }
      const p = presets[preset];
      const camera = new THREE.PerspectiveCamera(THREE.MathUtils.radToDeg(2 * Math.atan(18 / lens)), 1, .001, 1000);
      const azimuth = THREE.MathUtils.degToRad(p.yaw+yaw), elevation = THREE.MathUtils.degToRad(p.pitch+pitch);
      const target = convert(p.position).multiplyScalar(.01).sub(convert(p.back).multiplyScalar(p.distance));
      camera.position.copy(target).add(new THREE.Vector3(p.distance*Math.cos(azimuth)*Math.cos(elevation),p.distance*Math.sin(elevation),-p.distance*Math.sin(azimuth)*Math.cos(elevation)));
      const horizontal = new THREE.Vector3().crossVectors(new THREE.Vector3(0,1,0),camera.position.clone().sub(target)).normalize().multiplyScalar(pan);
      target.add(horizontal); target.y += lift; camera.position.add(horizontal); camera.position.y += lift;
      camera.lookAt(target);
      camera.zoom = zoom; camera.updateProjectionMatrix();
      // Downsample a larger render into the game-sized PNG. The 256px suit tile needs more
      // coverage samples than the portraits to keep thin printing and diagonal edges intact.
      const sampleScale = p.size <= 256 ? 4 : 3;
      renderer.setPixelRatio(1); renderer.setSize(p.size * sampleScale, p.size * sampleScale, false);
      renderer.setClearColor(0x000000, 0); renderer.outputEncoding = THREE.sRGBEncoding;
      renderer.physicallyCorrectLights = true;
      renderer.toneMapping = THREE.NoToneMapping;
      iconExposure.value = Math.pow(2, -.56644344 + exposure);
      scene.updateMatrixWorld(true);
      snapshot.root.traverse(node => { if (node.isSkinnedMesh) node.skeleton.update(); });
      renderer.render(scene, camera);
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = p.size;
      const ctx = canvas.getContext('2d'); ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(renderer.domElement, 0, 0, p.size, p.size);
      return canvas;
    } finally {
      if (!captured) freeSnapshot(snapshot);
      renderer?.renderLists.dispose();
      restoreFace?.();
    }
  }
  async function download(canvas, preset) {
    if (!presets[preset] || !canvas) throw new Error('Render an icon first.');
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    if (!blob) throw new Error('PNG encoding failed.');
    const url = URL.createObjectURL(blob), link = document.createElement('a');
    link.href = url; link.download = `Suit-icon-${preset}-${canvas.width}.png`;
    (dialog?.open ? dialog : document.body).appendChild(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }
  function previewStatus() {
    if (!currentCanvas || !dialog) return '';
    const actual = dialog.querySelector('.icon-preview')?.classList.contains('actual-pixels');
    return `${currentCanvas.width} × ${currentCanvas.height} saved PNG · ${actual ? 'actual pixels' : 'enlarged preview'}`;
  }
  function testResult(result) {
    testPending = false;
    if (!dialog) return;
    const button = dialog.querySelector('.icon-test');
    if (button) button.disabled = applyPending || !currentCanvas || dialog.querySelector('select').value !== 'suit';
    const applyButton = dialog.querySelector('.icon-apply');
    if (applyButton) applyButton.disabled = applyPending || !currentCanvas;
    const allButton = dialog.querySelector('.icon-apply-all');
    if (allButton) allButton.disabled = applyPending || !currentCanvas;
    updateButtons();
    dialog.querySelector('.icon-status').textContent = result?.message || 'Suit icon test did not return a result.';
  }
  function applyResult(result) {
    applyPending = false;
    if (!dialog) return;
    const button = dialog.querySelector('.icon-apply');
    if (button) button.disabled = testPending || !currentCanvas;
    const allButton = dialog.querySelector('.icon-apply-all');
    if (allButton) allButton.disabled = testPending || !currentCanvas;
    const testButton = dialog.querySelector('.icon-test');
    if (testButton) testButton.disabled = testPending || !currentCanvas || dialog.querySelector('select').value !== 'suit';
    updateButtons();
    dialog.querySelector('.icon-status').textContent = result?.message || 'Suit icon assignment did not return a result.';
  }
  function release() { currentCanvas = null; freeSnapshot(studioSnapshot); studioSnapshot = null; environment?.dispose(); environment = null; renderer?.dispose(); renderer?.forceContextLoss(); renderer = null; }
  function settings(role) { return {preset:role,...framing[role],...lighting,captured:studioSnapshot}; }
  function busy() { return testPending || applyPending; }
  function updateButtons() {
    if (!dialog) return;
    dialog.querySelectorAll('.icon-save,.icon-apply,.icon-apply-all,.icon-reset,.icon-light-reset,.icon-pose,.icon-template-export,.icon-template-import').forEach(button => {button.disabled=busy() || !currentCanvas;});
    dialog.querySelectorAll('input,select,.icon-card').forEach(control => {control.disabled=busy();});
    const test=dialog.querySelector('.icon-test'); if(test) test.disabled=busy() || !currentCanvas || selected!=='suit';
  }
  function open() {
    if (dialog) { dialog.showModal(); studioSnapshot=capture(); refresh(); refreshCards(); return; }
    const style = document.createElement('style');
    style.textContent = `#suit-icon-studio{box-sizing:border-box;width:min(850px,95vw);max-height:94vh;overflow:auto;border:1px solid #4b5260;border-radius:12px;background:#20252e;color:#e8ecf2;padding:22px;font:14px 'Segoe UI',sans-serif}#suit-icon-studio::backdrop{background:#000b}#suit-icon-studio header{display:flex;justify-content:space-between;align-items:center}#suit-icon-studio h2{margin:0;font-size:22px}#suit-icon-studio p{color:#abb7c7;line-height:1.5}#suit-icon-studio .icon-grid{display:grid;grid-template-columns:minmax(180px,1fr) 235px;gap:22px}#suit-icon-studio .icon-preview{aspect-ratio:1;display:grid;place-items:center;background:repeating-conic-gradient(#303641 0% 25%,#262c35 0% 50%) 0/24px 24px;border-radius:8px;overflow:hidden}#suit-icon-studio canvas{width:auto;height:auto;max-width:100%;max-height:100%;object-fit:contain}#suit-icon-studio label{display:block;margin:0 0 18px}#suit-icon-studio select,#suit-icon-studio input{display:block;width:100%;margin-top:8px}#suit-icon-studio button,#suit-icon-studio select{font:inherit;background:#303845;color:#eee;border:1px solid #515e70;border-radius:6px;padding:9px 12px}#suit-icon-studio button{cursor:pointer}#suit-icon-studio button:disabled{opacity:.45;cursor:default}#suit-icon-studio .icon-save{background:#ffda43;color:#171b22;font-weight:600;width:100%;margin:14px 0}#suit-icon-studio small{display:block;color:#aeb8c8;line-height:1.5}@media(max-width:600px){#suit-icon-studio .icon-grid{grid-template-columns:1fr}#suit-icon-studio .icon-preview{max-height:42vh;justify-self:center;width:min(100%,42vh)}}`;
    style.textContent += '#suit-icon-studio .icon-stage{min-width:0}#suit-icon-studio .icon-preview canvas{width:100%;height:100%}#suit-icon-studio .icon-preview.actual-pixels canvas{width:auto;height:auto;max-width:100%;max-height:100%}#suit-icon-studio .icon-size-toggle{margin-top:8px}';
    style.textContent += `#suit-icon-studio{width:min(1080px,96vw);height:min(860px,94vh);padding:18px;overflow:hidden;display:grid;grid-template-rows:auto auto minmax(0,1fr) auto;gap:12px}#suit-icon-studio:not([open]){display:none}#suit-icon-studio p{margin:0}#suit-icon-studio .icon-grid{min-height:0;grid-template-columns:minmax(220px,1fr) 300px;gap:16px}#suit-icon-studio .icon-stage{display:flex;flex-direction:column;min-height:0;gap:8px}#suit-icon-studio .icon-preview{flex:1;min-height:120px;aspect-ratio:auto}#suit-icon-studio .icon-preview canvas{object-fit:contain}#suit-icon-studio .icon-controls{overflow:auto;padding:0 6px 0 0}#suit-icon-studio label{margin-bottom:12px}#suit-icon-studio input{margin-top:5px}#suit-icon-studio details{padding:12px;background:#292f39;border-radius:8px;margin-bottom:10px}#suit-icon-studio summary{cursor:pointer;font-weight:600;margin-bottom:12px}#suit-icon-studio .icon-cards{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:8px}#suit-icon-studio .icon-card{padding:6px;font-size:12px;text-align:left}#suit-icon-studio .icon-card[aria-pressed=true]{border-color:#b756ff;background:#42304f}#suit-icon-studio .icon-card canvas{width:100%;aspect-ratio:1;display:block;background:#252a32;border-radius:4px}#suit-icon-studio .icon-card span{display:block;margin-top:5px}#suit-icon-studio .icon-actions{display:flex;align-items:center;gap:8px;flex-wrap:wrap;border-top:1px solid #424b59;padding-top:12px}#suit-icon-studio .icon-apply-all{background:#b94dff;color:#10131a;font-weight:600}#suit-icon-studio .icon-status{flex-basis:100%;min-height:21px}#suit-icon-studio .icon-save{width:auto;margin:0;background:#303845;color:#eee;font-weight:400}#suit-icon-studio output{float:right;color:#dce6f3;font-variant-numeric:tabular-nums}#suit-icon-studio .icon-size-toggle{align-self:flex-start;margin:0}@media(max-width:700px){#suit-icon-studio{overflow:auto;height:auto;max-height:94vh;display:block}#suit-icon-studio .icon-grid{display:block;margin-top:12px}#suit-icon-studio .icon-preview{height:36vh}#suit-icon-studio .icon-controls{overflow:visible;margin-top:12px}#suit-icon-studio .icon-actions{margin-top:12px}}`;
    style.textContent += '#suit-icon-studio .icon-preview{position:relative;display:block}#suit-icon-studio .icon-preview canvas{position:absolute;inset:0;width:100%;height:100%}#suit-icon-studio .icon-preview.actual-pixels canvas{inset:auto;top:50%;left:50%;transform:translate(-50%,-50%);width:auto;height:auto;max-width:100%;max-height:100%}';
    document.head.appendChild(style);
    dialog = document.createElement('dialog'); dialog.id = 'suit-icon-studio';
    const slider=(key,label,min,max,step,scope='camera')=>`<label>${label}<output data-output="${key}"></output><input data-key="${key}" data-scope="${scope}" aria-label="${label}" type="range" min="${min}" max="${max}" step="${step}"></label>`;
    dialog.innerHTML = `<header><h2>Character icon studio</h2><button type="button" aria-label="Close icon studio">Close</button></header><p>Frame each native slot · template-based cameras · transparent PNGs</p><div class="icon-grid"><div class="icon-preview"></div><div class="icon-controls"><label>Editing slot<select aria-label="Icon layout"></select></label><details open><summary>Camera & framing · this slot</summary>${slider('zoom','Icon zoom',.5,1.5,.01)}${slider('pan','Horizontal framing',-.4,.4,.005)}${slider('lift','Icon vertical framing',-.35,.35,.005)}${slider('yaw','Camera angle',-55,55,1)}${slider('pitch','Camera height angle',-30,30,1)}${slider('lens','Lens (mm)',50,150,1)}<button type="button" class="icon-reset">Reset this camera</button></details><details open><summary>Lighting · all slots</summary>${slider('exposure','Icon exposure',-2,2,.1,'light')}${slider('cool','Blue rim strength',0,3,.05,'light')}${slider('warm','Warm rim strength',0,3,.05,'light')}<button type="button" class="icon-light-reset">Reset lighting</button></details><button type="button" class="icon-pose">Refresh captured pose</button><p><small>Each slot keeps its own camera. Lighting is shared. Left/right label the game's slots, not the direction the portrait faces. Hidden helpers and gliders are excluded. Materials and Cycles lighting are approximated.</small></p></div></div><footer class="icon-actions"><button type="button" class="icon-save">Create selected PNG…</button><small class="icon-status" role="status"></small></footer>`;
    dialog.querySelector('.icon-light-reset').insertAdjacentHTML('beforebegin',slider('key','White / key strength',0,3,.05,'light'));
    dialog.querySelector('input[data-key="warm"]').step='.01';
    const preview = dialog.querySelector('.icon-preview'), stage = document.createElement('div');
    stage.className = 'icon-stage'; preview.replaceWith(stage); stage.appendChild(preview);
    const sizeToggle = document.createElement('button');
    sizeToggle.type = 'button'; sizeToggle.className = 'icon-size-toggle'; sizeToggle.textContent = 'Actual pixels';
    sizeToggle.onclick = () => {
      const actual = preview.classList.toggle('actual-pixels');
      sizeToggle.textContent = actual ? 'Fit preview' : 'Actual pixels';
      dialog.querySelector('.icon-status').textContent = previewStatus();
    };
    stage.appendChild(sizeToggle);
    const cards=document.createElement('div'); cards.className='icon-cards';
    for(const [role,p] of Object.entries(presets)) {
      const button=document.createElement('button');button.type='button';button.className='icon-card';button.dataset.role=role;
      button.setAttribute('aria-label',`Edit ${p.label}`);button.innerHTML=`<canvas width="128" height="128"></canvas><span>${p.label} · ${p.size}px</span>`;
      button.onclick=()=>{if(busy())return;select.value=role;select.onchange();};cards.appendChild(button);
    }
    stage.appendChild(cards);
    const templates=document.createElement('details');
    templates.innerHTML='<summary>Share a studio template</summary><label>Template name<input class="icon-template-name" maxlength="80" value="My icon studio"></label><button type="button" class="icon-template-export">Export settings JSON…</button> <button type="button" class="icon-template-import">Import settings JSON…</button><input type="file" class="icon-template-file" accept=".json,application/json" hidden><small>Cameras and lighting only. No character assets, images, file paths or pose are included. Import previews settings; it does not assign icons.</small>';
    dialog.querySelector('.icon-controls').appendChild(templates);
    const templateFile=templates.querySelector('.icon-template-file');
    templates.querySelector('.icon-template-export').onclick=()=>{if(busy())return;try{exportTemplate();dialog.querySelector('.icon-status').textContent='Choose where to save the shared camera and lighting template.';}catch(error){dialog.querySelector('.icon-status').textContent=error.message;}};
    templates.querySelector('.icon-template-import').onclick=()=>{if(busy())return;templateFile.value='';templateFile.click();};
    templateFile.onchange=async()=>{const file=templateFile.files?.[0];if(!file||busy())return;try{if(file.size>65536)throw new Error('Template must be smaller than 64 KiB.');importTemplate(JSON.parse(await file.text()));}catch(error){dialog.querySelector('.icon-status').textContent='Template not changed: '+error.message;}};
    if (canTestSuit && layoutKey && typeof post === 'function') {
      const testButton = document.createElement('button');
      testButton.type = 'button'; testButton.className = 'icon-test';
      testButton.textContent = 'Test suit icon cook';
      testButton.title = 'Cook a separate 256px BC7 test texture. Does not change this suit, its icons or the game.';
      function submitSuitIcon(type) {
        if (testPending || applyPending || !currentCanvas || select.value !== 'suit') return;
        const png = currentCanvas.toDataURL('image/png');
        if (png.length > 1_500_000) {
          dialog.querySelector('.icon-status').textContent = 'The generated PNG is too large for an in-app icon cook. Save it and import manually.';
          return;
        }
        if (type === 'apply-suit-icon') applyPending = true; else testPending = true;
        updateButtons();
        dialog.querySelector('.icon-status').textContent = type === 'apply-suit-icon'
          ? 'Waiting for confirmation in Batcomputer…' : 'Cooking a separate test icon… Your saved suit and game are untouched.';
        try { post({ type, layout: layoutKey, png }); }
        catch (error) {
          const result = { message: 'Could not send the icon: ' + error.message };
          if (type === 'apply-suit-icon') applyResult(result); else testResult(result);
        }
      }
      testButton.onclick = () => submitSuitIcon('test-suit-icon');
      const applyButton = document.createElement('button');
      applyButton.type = 'button'; applyButton.className = 'icon-apply';
      applyButton.textContent = 'Create and assign selected icon';
      applyButton.title = 'Cook this layout at its native size and save its assignment. Does not install a mod.';
      const allButton = document.createElement('button');
      allButton.type = 'button'; allButton.className = 'icon-apply-all';
      allButton.textContent = 'Create and assign all character icons';
      allButton.title = 'Menu, left, right (512px) and suit tile (256px). All must cook before the project is updated.';
      async function submitIcons(all) {
        if (testPending || applyPending || !currentCanvas) return;
        applyPending = true; updateButtons();
        const selected = select.value;
        dialog.querySelector('.icon-status').textContent = all ? 'Creating all four icons…' : 'Creating the selected icon…';
        try {
          const icons = {};
          for (const role of all ? Object.keys(presets) : [selected]) {
            await new Promise(resolve => requestAnimationFrame(resolve));
            if (!dialog.open) throw new Error('Studio closed before the icon set was sent.');
            const canvas = render(settings(role));
            const png = canvas.toDataURL('image/png');
            if (png.length > 1_500_000) throw new Error('An icon is too large. Save the PNG and import it manually.');
            icons[role] = png;
          }
          dialog.querySelector('.icon-status').textContent = 'Waiting for confirmation in Batcomputer…';
          post({type:'apply-character-icons',layout:layoutKey,icons});
        } catch (error) { applyResult({message:error.message}); }
      }
      applyButton.onclick = () => submitIcons(false); allButton.onclick = () => submitIcons(true);
      dialog.querySelector('.icon-save').before(testButton);
      dialog.querySelector('.icon-save').before(applyButton);
      dialog.querySelector('.icon-save').before(allButton);
      const note = document.createElement('small');
      note.textContent = 'Assignment cooks and saves the project. Rebuild the mod to use the icons in-game. Existing textures are kept.';
      dialog.querySelector('.icon-controls').appendChild(note);
    }
    document.body.appendChild(dialog);
    const select = dialog.querySelector('select');
    for (const [id,p] of Object.entries(presets)) { const option = document.createElement('option'); option.value = id; option.textContent = `${p.label} · ${p.size}px`; select.appendChild(option); }
    select.value=selected;
    select.onchange = () => { selected=select.value; syncInputs(); refresh(); };
    const inputs = [...dialog.querySelectorAll('input[data-key]')];
    inputs.forEach(input => { input.oninput = () => {
      if(busy())return;
      (input.dataset.scope==='light'?lighting:framing[selected])[input.dataset.key]=Number(input.value);
      syncInputs();cancelAnimationFrame(pending);pending=requestAnimationFrame(()=>{refresh();if(input.dataset.scope==='light')refreshCards();});
    }; });
    const close = dialog.querySelector('header button'); close.onclick = () => dialog.close();
    dialog.addEventListener('close', () => { cancelAnimationFrame(pending); release(); });
    dialog.querySelector('.icon-reset').onclick = () => { framing[selected]=defaults(selected);syncInputs();refresh(); };
    dialog.querySelector('.icon-light-reset').onclick = () => {if(busy())return;Object.assign(lighting,lightDefaults);syncInputs();refresh();refreshCards();};
    dialog.querySelector('.icon-pose').onclick = () => {if(busy())return;freeSnapshot(studioSnapshot);studioSnapshot=capture();refresh();refreshCards();};
    dialog.querySelector('.icon-save').onclick = async () => {
      const button = dialog.querySelector('.icon-save'); button.disabled = true;
      try { await download(currentCanvas, select.value); dialog.querySelector('.icon-status').textContent = 'Choose where to save your PNG. Your suit project is unchanged.'; }
      catch (error) { dialog.querySelector('.icon-status').textContent = error.message; }
      finally { button.disabled = !currentCanvas; }
    };
    dialog.showModal();studioSnapshot=capture();syncInputs();refresh();refreshCards();
  }
  function syncInputs() {
    if(!dialog)return;
    for(const input of dialog.querySelectorAll('input[data-key]')) {
      const value=(input.dataset.scope==='light'?lighting:framing[selected])[input.dataset.key];input.value=value;
      dialog.querySelector(`[data-output="${input.dataset.key}"]`).textContent=Number(value).toFixed(input.step>=1?0:2);
    }
    for(const card of dialog.querySelectorAll('.icon-card'))card.setAttribute('aria-pressed',String(card.dataset.role===selected));
  }
  function setCard(role,canvas) {
    const thumb=dialog.querySelector(`.icon-card[data-role="${role}"] canvas`);thumb.getContext('2d').drawImage(canvas,0,0,128,128);
  }
  function refreshCards() {
    if(!dialog?.open || busy())return;
    try {for(const role of Object.keys(presets))if(role!==selected)setCard(role,render(settings(role)));}
    catch(error){dialog.querySelector('.icon-status').textContent=error.message;}
  }
  function refresh() {
    if (!dialog?.open) return;
    const status = dialog.querySelector('.icon-status'), save = dialog.querySelector('.icon-save');
    try {
      currentCanvas = render(settings(selected));
      dialog.querySelector('.icon-preview').replaceChildren(currentCanvas); save.disabled = false;
      setCard(selected,currentCanvas);
      const testButton = dialog.querySelector('.icon-test');
      if (testButton) testButton.disabled = testPending || applyPending || dialog.querySelector('select').value !== 'suit';
      const applyButton = dialog.querySelector('.icon-apply');
      if (applyButton) applyButton.disabled = testPending || applyPending;
      const allButton = dialog.querySelector('.icon-apply-all');
      if (allButton) allButton.disabled = testPending || applyPending;
      status.textContent = previewStatus();
      updateButtons();
    } catch (error) { currentCanvas = null; save.disabled = true; dialog.querySelectorAll('.icon-test,.icon-apply,.icon-apply-all').forEach(button => { button.disabled = true; }); dialog.querySelector('.icon-preview').replaceChildren(); status.textContent = error.message; }
  }
  function templateSettings() {
    return {format:'batcomputer.character-icon-studio',version:2,cameraBasis:'keyed-native-icon-cameras-v1',name:dialog?.querySelector('.icon-template-name')?.value.trim()||'Icon studio',cameras:Object.fromEntries(Object.entries(framing).map(([role,values])=>[role,{...values}])),lighting:{...lighting}};
  }
  function importTemplate(value) {
    if(busy())throw new Error('Wait for the current icon operation to finish.');
    if(!value || value.format!=='batcomputer.character-icon-studio' || ![1,2].includes(value.version) || value.cameraBasis!=='keyed-native-icon-cameras-v1')throw new Error('Unsupported icon-studio template format or version.');
    if(typeof value.name!=='string'||value.name.length>80)throw new Error('Template name must be 80 characters or fewer.');
    const cameraRanges={zoom:[.5,1.5],lift:[-.35,.35],pan:[-.4,.4],yaw:[-55,55],pitch:[-30,30],lens:[50,150]},lightRanges={exposure:[-2,2],cool:[0,3],warm:[0,3],...(value.version===2 ? {key:[0,3]} : {})};
    const read=(values,ranges)=>{if(!values||typeof values!=='object'||Array.isArray(values)||Object.keys(values).length!==Object.keys(ranges).length)throw new Error('Invalid template settings.');const copy={};for(const [key,[min,max]] of Object.entries(ranges)){const v=values[key];if(typeof v!=='number'||!Number.isFinite(v)||v<min||v>max)throw new Error('Unsupported '+key+' setting.');copy[key]=v;}return copy;};
    if(!value.cameras||Object.keys(value.cameras).sort().join(',')!==Object.keys(presets).sort().join(','))throw new Error('Template must contain all four native camera slots.');
    const cameras=Object.fromEntries(Object.keys(presets).map(role=>[role,read(value.cameras[role],cameraRanges)])),newLighting={key:1,...read(value.lighting,lightRanges)};
    // Validate the complete file before changing a single setting.
    for(const role of Object.keys(presets))framing[role]=cameras[role];Object.assign(lighting,newLighting);
    if(dialog){dialog.querySelector('.icon-template-name').value=value.name;syncInputs();refresh();refreshCards();dialog.querySelector('.icon-status').textContent='Imported '+value.name+'. Preview only; your assigned icons are unchanged.';}
  }
  function exportTemplate() {
    const blob=new Blob([JSON.stringify(templateSettings(),null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=document.createElement('a');
    link.href=url;link.download='Icon-studio-settings.icon-studio.json';(dialog?.open ? dialog : document.body).appendChild(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
  }
  return { open, render, download, testResult, applyResult, dispose:release, presets, templateSettings, importTemplate, exportTemplate };
};
