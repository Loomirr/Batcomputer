// Item workshop uses the character viewer's resolved material data and structural shaders.
const scene = new THREE.Scene(), camera = new THREE.PerspectiveCamera(40, innerWidth / innerHeight, .001, 1000);
const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight); renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setClearColor(0x303944); renderer.outputEncoding = THREE.sRGBEncoding;
renderer.toneMapping = THREE.ACESFilmicToneMapping; renderer.toneMappingExposure = 1;
document.body.appendChild(renderer.domElement);
scene.add(new THREE.HemisphereLight(0xd9eaff, 0x343238, .8));
for (const [x,y,z,p] of [[2,3,4,1.4],[-3,2,-2,.7],[0,3,-4,.6]]) {
  const l = new THREE.DirectionalLight(0xffffff,p); l.position.set(x,y,z); scene.add(l);
}
// Broad studio reflection cards ensure metallic materials do not turn black without an environment.
const studio = new THREE.Scene(); studio.background = new THREE.Color(0x555c68);
for (const [x,y,z,w,h] of [[3,2,0,2,4],[-3,1,-1,2,3],[0,4,0,4,2]]) {
  const card = new THREE.Mesh(new THREE.PlaneGeometry(w,h), new THREE.MeshBasicMaterial({color:0xffffff,side:THREE.DoubleSide}));
  card.position.set(x,y,z); card.lookAt(0,0,0); studio.add(card);
}
const pmrem = new THREE.PMREMGenerator(renderer), environment = pmrem.fromScene(studio);
scene.environment = environment.texture; pmrem.dispose();
studio.traverse(n => { n.geometry?.dispose(); n.material?.dispose(); });
const axes = new THREE.AxesHelper(.15), grid = new THREE.GridHelper(2,20,0x637080,0x414c59);
scene.add(axes,grid);
const reference = new THREE.Group(); scene.add(reference);
const effectRoot = new THREE.Group(); scene.add(effectRoot);
let custom = null, customFile = '', revision = -1, busy = false, firstCustom = true;
const loader = new THREE.GLTFLoader(), controls = new THREE.OrbitControls(camera,renderer.domElement);
controls.enableDamping = true; controls.dampingFactor = .08; camera.position.set(.6,.4,.6);
const textures = new Map(), textureLoader = new THREE.TextureLoader(), referenceModels = [];
const math = window.BatcomputerItemTransformMath(THREE), effectMath = window.BatcomputerItemTransformMath(THREE, true);
const gizmo = new THREE.TransformControls(camera, renderer.domElement); gizmo.setSize(.85); scene.add(gizmo);
// The saved OBJ/effect format has ONE size multiplier. Only expose proportional scale handles.
for (const group of [gizmo.children[0].gizmo.scale, gizmo.children[0].picker.scale])
  [...group.children].filter(h => !h.name.startsWith('XYZ')).forEach(h => group.remove(h));
const bar = document.getElementById('bar'), tools = document.createElement('div'); tools.id = 'transformTools'; tools.hidden = true; bar.appendChild(tools);
const make = (tag, text, parent = tools) => { const n = document.createElement(tag); if (text) n.textContent = text; parent.appendChild(n); return n; };
let selected = null, dragging = false, beforeDrag = null, enabled = true, pending = null;
const modeButtons = new Map();
function setMode(mode) { if (dragging) return; gizmo.setMode(mode); modeButtons.forEach((b,k) => b.classList.toggle('active', k === mode)); }
for (const [mode,label] of [['translate','Move · W'],['rotate','Rotate · R'],['scale','Size · E']]) {
  const b = make('button',label); b.onclick = () => setMode(mode); modeButtons.set(mode,b);
}
const space = make('select'); space.setAttribute('aria-label','Transform coordinate space');
for (const [value,label] of [['world','World axes'],['local','Local axes']]) { const o = make('option',label,space); o.value = value; }
space.onchange = () => gizmo.setSpace(space.value); space.onchange();
const snapLabel = make('label','Snap'), snap = make('input',null,snapLabel); snap.type = 'checkbox'; snap.setAttribute('aria-label','Snap move 1 cm, rotation 15 degrees, size 0.1');
snap.onchange = () => { gizmo.setTranslationSnap(snap.checked ? .01 : null); gizmo.setRotationSnap(snap.checked ? Math.PI / 12 : null); gizmo.setScaleSnap(snap.checked ? .1 : null); };
const hud = make('div',null,document.body);hud.id='workshopHud';
const reset = make('button','Reset transform'), readout = make('span',null,hud); readout.id = 'readout';
const matchSize = make('button','Match original size'); matchSize.title = 'Explicitly resize your model to the original longest dimension; never changes the original';
const centerModel = make('button','Center on original'); centerModel.title = 'Move your centered OBJ to the original bounding-box center';
const compare = make('select',null,bar); compare.setAttribute('aria-label','Original model appearance');
for (const [value,label] of [['ghost','Original: faded'],['solid','Original: materials'],['wire','Original: wireframe']]) { const o=make('option',label,compare);o.value=value; }
compare.value='ghost';compare.onchange=()=>applyReferenceAppearance();
const framing = make('select',null,bar); framing.setAttribute('aria-label','Camera framing target');
for (const [value,label] of [['both','Frame both'],['custom','Frame custom'],['original','Frame original']]) { const o=make('option',label,framing);o.value=value; }
framing.value='both';framing.onchange=()=>frame();
const dimensions = make('div',null,hud);dimensions.id='dimensions';hud.appendChild(document.getElementById('help'));
function applyReferenceAppearance() {
  reference.traverse(o=> { if(!o.isMesh)return; for(const m of Array.isArray(o.material)?o.material:[o.material]) {
    m.userData.workshopOriginal ||= {opacity:m.opacity,transparent:m.transparent,depthWrite:m.depthWrite,wireframe:m.wireframe};
    Object.assign(m,m.userData.workshopOriginal);
    if(custom?.visible && compare.value !== 'solid') {m.opacity=.24;m.transparent=true;m.depthWrite=false;m.wireframe=compare.value === 'wire';}
    m.needsUpdate=true;
  }});
}
function updateDimensions() {
  const size = root => { if(!root)return 'not loaded'; const v=new THREE.Box3().setFromObject(root).getSize(new THREE.Vector3()); return [v.x,v.z,v.y].map(v=>(v*100).toFixed(1)).join(' × ')+' cm'; };
  dimensions.textContent=`Original: ${size(reference)}  |  Your model: ${size(custom)} · actual mesh dimensions, not camera zoom`;
}
matchSize.onclick=()=> {
  if(!custom || selected?.target !== 'custom' || pending || !enabled)return;
  const a=new THREE.Box3().setFromObject(reference).getSize(new THREE.Vector3()),b=new THREE.Box3().setFromObject(custom).getSize(new THREE.Vector3());
  const longest=v=>Math.max(v.x,v.y,v.z); if(longest(b)<=0 || longest(a)<=0)return;
  const t=math.fromNode(custom);t.scale=Math.max(.001,Math.min(1000,t.scale*longest(a)/longest(b)));
  math.toNode(custom,t);updateReadout();updateDimensions();commitTransform();
};
centerModel.onclick=()=> {
  if(!custom || selected?.target !== 'custom' || pending || !enabled)return;
  const box=new THREE.Box3().setFromObject(reference); if(box.isEmpty())return;
  const t=math.fromNode(custom),p=box.getCenter(new THREE.Vector3());t.offset=[p.x*100,-p.z*100,p.y*100];
  if(!math.valid(t))return;math.toNode(custom,t);updateReadout();updateDimensions();commitTransform();
};
function updateReadout() {
  if (!selected) return; const t = selected.math.fromNode(selected.node), f = v => Number(v.toFixed(3));
  readout.textContent = `${selected.target === 'effect' ? 'Selected effect' : 'Custom OBJ'} · XYZ (cm): ${t.offset.map(f).join(', ')} · Pitch / Yaw / Roll (°): ${t.rotation.map(f).join(', ')} · Uniform size: ${f(t.scale)}`;
  axes.position.copy(selected.node.position);
}
function selectTransform(node, target, index, version, transform) {
  if (dragging) return;
  gizmo.detach(); selected = node ? { node, target, index, revision:version, math:target === 'effect' ? effectMath : math } : null;
  tools.hidden = !selected;
  matchSize.hidden = centerModel.hidden = selected?.target !== 'custom';
  if (!selected) { axes.position.set(0,0,0); readout.textContent=''; return; }
  selected.math.toNode(node,transform); selected.loaded = selected.math.clone(transform);
  gizmo.enabled = enabled && !pending; if (gizmo.enabled) gizmo.attach(node); updateReadout();
}
function commitTransform() {
  if (!selected || !enabled) return;
  const t = selected.math.fromNode(selected.node); if (!selected.math.valid(t)) return;
  // One host update per gesture, not a bake/export per mouse move. Never accept a stale revision.
  pending = { target:selected.target, revision:selected.revision }; gizmo.detach();
  window.chrome?.webview?.postMessage({ type:'item-workshop-transform', target:selected.target, index:selected.index, revision:selected.revision, transform:t });
}
reset.onclick = () => { if (!selected || pending || !enabled) return; selected.math.toNode(selected.node,{scale:1,offset:[0,0,0],rotation:[0,0,0]}); updateReadout(); commitTransform(); };
gizmo.addEventListener('dragging-changed',e => { dragging = e.value; controls.enabled = !dragging; });
gizmo.addEventListener('mouseDown',() => { if (selected) beforeDrag = selected.math.fromNode(selected.node); });
gizmo.addEventListener('objectChange',() => {
  if (!selected) return;
  if (gizmo.mode === 'scale') selected.node.scale.setScalar(Math.max(selected.target === 'effect' ? .01 : .001, Math.min(selected.target === 'effect' ? 10 : 1000, selected.node.scale.x)));
  if (!selected.math.valid(selected.math.fromNode(selected.node))) selected.math.toNode(selected.node,beforeDrag || selected.loaded);
  updateReadout();
  updateDimensions();
});
gizmo.addEventListener('mouseUp',() => { if (beforeDrag) { commitTransform(); beforeDrag = null; } });
window.itemWorkshopEditor = {
  setEnabled(value) { enabled = value; gizmo.enabled = value && !pending; tools.querySelectorAll('button,select,input').forEach(n => n.disabled = !value); if (!value) gizmo.detach(); else if (selected && !pending) gizmo.attach(selected.node); },
  previewFailed(message) { pending = null; if(selected && enabled) { gizmo.enabled = true; gizmo.attach(selected.node); } report(message); }
};
setMode('translate');
function tex(path,color=false) {
  if (!path) return null; const key = path+'|'+color;
  if (textures.has(key)) return textures.get(key);
  const t = textureLoader.load(path,undefined,undefined,() => report('Texture unavailable: '+path));
  t.flipY = false; t.encoding = color ? THREE.sRGBEncoding : THREE.LinearEncoding;
  t.anisotropy = renderer.capabilities.getMaxAnisotropy(); textures.set(key,t); return t;
}
function dress(root,slots) {
  let index = 0;
  root.traverse(o => {
    if (!o.isMesh) return;
    const uv = o.geometry.attributes.uv;
    if (uv && !o.geometry.attributes.aUv0) o.geometry.setAttribute('aUv0',uv);
    const source = Array.isArray(o.material) ? o.material : [o.material];
    const materials = source.map(old => {
      const stable = /^BC_SLOT_(\d+)_/.exec(old?.name || '');
      const s = slots[stable ? Number(stable[1]) : index] || slots[0] || {}; index++;
      const m = new THREE.MeshPhysicalMaterial({color:s.col || (s.tex ? 0xffffff : old?.color || 0x9aa0a8),roughness:s.rough ?? old?.roughness ?? .55,metalness:s.metal ?? old?.metalness ?? 0,side:THREE.DoubleSide});
      if(s.cut && s.col)m.color.convertSRGBToLinear();
      m.name = old?.name || ''; m.map = tex(s.tex,true); m.normalMap = tex(s.nrm);
      if (m.normalMap) m.normalScale.set(1,-1);
      const orm = tex(s.mmr);
      if (orm) { m.roughnessMap = m.metalnessMap = orm; m.roughness = m.metalness = 1; }
      const ao = tex(s.ao);
      if (ao && uv) { o.geometry.setAttribute('uv2',uv); m.aoMap=ao; m.aoMapIntensity=.65; }
      if (s.nrm2 && uv) {
        const micro = tex(s.micro); if (micro) micro.wrapS = micro.wrapT = THREE.RepeatWrapping;
        window.BatcomputerCharacterNormals(THREE,m,tex(s.nrm2),micro,s.microTile,s.microStrength);
      }
      if (s.rao && uv) window.BatcomputerCharacterSurface(THREE,m,tex(s.rao));
      m.envMapIntensity = .5;
      if (!s.tex && s.col && !orm) { m.reflectivity=.04; m.envMapIntensity=.1; m.roughness=Math.max(.55,m.roughness); }
      if (s.alpha) { m.alphaMap=tex(s.alpha); m.alphaTest=.4; }
      if (s.cut) { m.alphaTest=.5; m.polygonOffset=true; m.polygonOffsetFactor=-2; m.polygonOffsetUnits=-2; }
      m.vertexColors=false; m.skinning=!!o.isSkinnedMesh;
      old?.dispose(); return m;
    });
    o.material = Array.isArray(o.material) ? materials : materials[0];
    o.visible = !slots.some((s,i) => s.hide && i === index-1);
  });
}
function report(message) { document.getElementById('error').textContent = message; }
function bounds(target = framing.value) {
  const box = new THREE.Box3(); if(target !== 'custom' && reference.visible)box.setFromObject(reference);
  if(target !== 'original' && custom?.visible)box.union(new THREE.Box3().setFromObject(custom));
  if(target === 'both' && effectRoot.children.length)box.union(new THREE.Box3().setFromObject(effectRoot));
  return box;
}
function frame(direction) {
  const box=bounds(); if(box.isEmpty()){report('The framing target is hidden or not loaded. Show that model in the Model panel first.');return;}
  const center=box.getCenter(new THREE.Vector3()), size=Math.max(box.getSize(new THREE.Vector3()).length(),.05);
  const vectors={front:[0,0,1],side:[1,0,0],top:[0,1,.001],perspective:[1,.65,1]};
  const halfFov=camera.fov*Math.PI/360, limitingFov=Math.min(halfFov,Math.atan(Math.tan(halfFov)*camera.aspect)),distance=size*.5/Math.sin(limitingFov)*1.12;
  controls.target.copy(center); camera.position.copy(center).add(new THREE.Vector3(...(vectors[direction||document.getElementById('view').value]||vectors.perspective)).normalize().multiplyScalar(distance));
  grid.position.y=box.min.y-.001; grid.scale.setScalar(Math.max(size, .1)); axes.scale.setScalar(Math.max(size,.1)); controls.update();
}
document.getElementById('frame').onclick=()=>frame();
document.getElementById('view').onchange=e=>frame(e.target.value);
document.getElementById('grid').onchange=e=>grid.visible=e.target.checked;
document.getElementById('axes').onchange=e=>axes.visible=e.target.checked;
document.getElementById('exposure').oninput=e=>renderer.toneMappingExposure=Number(e.target.value);
addEventListener('keydown',e=>{
  if (/^(INPUT|SELECT|TEXTAREA)$/.test(e.target.tagName) || e.target.isContentEditable || e.ctrlKey || e.metaKey || e.altKey || dragging) return;
  const key = e.key.toLowerCase(); if(key === 'f') { e.preventDefault(); frame(); }
  if (selected && enabled && !pending && {w:'translate',r:'rotate',e:'scale'}[key]) { e.preventDefault(); setMode({w:'translate',r:'rotate',e:'scale'}[key]); }
});
const referenceReady=Promise.all((window.PREVIEW_MODELS||[]).map(async info=>{
  const g=await loader.loadAsync(info.file); dress(g.scene,info.slots||[]); reference.add(g.scene); referenceModels.push({root:g.scene,info});
})).then(()=>{frame();updateDimensions();}).catch(e=>report('Reference failed: '+e));
function disposeModel(root) { root.traverse(n=>{n.geometry?.dispose(); for(const m of Array.isArray(n.material)?n.material:[n.material])m?.dispose();}); }
async function poll() {
  if(busy || dragging)return; busy=true;
  try {
    await referenceReady;
    const s=await (await fetch('weapon.json?t='+Date.now(),{cache:'no-store'})).json();
    if(dragging || (pending?.target === 'custom' && s.revision <= pending.revision))return;
    reference.visible=!!s.original; if(custom)custom.visible=!!s.custom;
    if(revision===s.revision)return;
    if(s.file) {
      if (s.file !== customFile) {
        const g=await loader.loadAsync(s.file);
        if(dragging || (pending?.target === 'custom' && s.revision <= pending.revision)){disposeModel(g.scene);return;}
        gizmo.detach(); if(custom){scene.remove(custom);disposeModel(custom);}
        custom=g.scene; customFile=s.file; scene.add(custom);
      }
      dress(custom,s.slots||[]); custom.visible=!!s.custom;
      if (s.transform && math.valid(s.transform)) math.toNode(custom,s.transform);
      if (pending?.target === 'custom') pending = null;
      if (selected?.target !== 'effect') selectTransform(s.editable && s.custom ? custom : null,'custom',-1,s.revision,s.transform);
      // Keep the user's camera on import. Explicit framing never alters either model's transform.
      if(firstCustom){firstCustom=false;if(!reference.visible)frame();}
    } else { if(custom)custom.visible=false; if(selected?.target === 'custom')selectTransform(null); }
    // Restore baseline for unassigned slots; an empty override is not a stale cached material.
    for(const model of referenceModels) dress(model.root,(model.info.slots||[]).map((slot,i)=>s.originalSlots?.[i]||slot));
    applyReferenceAppearance();updateDimensions();
    revision=s.revision; report('');
    document.getElementById('mode').textContent=s.file?'Custom model · live material preview':'Native model · live material preview';
  } catch(e) { report(String(e)); } finally { busy=false; }
}
setInterval(poll,400);
let effectRevision = -1, effectBusy = false, firstEffects = true;
async function pollEffects() {
  if(effectBusy || dragging)return; effectBusy=true;
  try {
    const state = await (await fetch('effects.json?t='+Date.now(),{cache:'no-store'})).json();
    if(dragging || state.revision === effectRevision || (pending?.target === 'effect' && state.revision <= pending.revision))return;
    if(selected?.target === 'effect')selectTransform(null);
    while(effectRoot.children.length) { const n=effectRoot.children[0];effectRoot.remove(n);disposeModel(n); }
    for(const e of state.effects || []) {
      const g=new THREE.Group(); effectMath.toNode(g,{scale:e.Scale,offset:[e.X,e.Y,e.Z],rotation:[e.Pitch,e.Yaw,e.Roll]});
      const a=new THREE.AxesHelper(.12); a.material.depthTest=false; a.renderOrder=20; g.add(a);
      const marker=new THREE.Mesh(new THREE.SphereGeometry(.02,10,8),new THREE.MeshBasicMaterial({color:0xffffff,wireframe:true,depthTest:false})); marker.renderOrder=20; g.add(marker);
      for(let i=0;i<14;i++) { const m=new THREE.Mesh(new THREE.SphereGeometry(e.Shape==='cloud'?.028:.008,6,4),new THREE.MeshBasicMaterial({color:e.Color,transparent:true,opacity:.35,depthWrite:false,depthTest:false})); m.renderOrder=19;m.userData={particle:true,i,shape:e.Shape};g.add(m); }
      effectRoot.add(g);
    }
    effectRevision=state.revision; if(pending?.target === 'effect')pending=null;
    const node=effectRoot.children[state.selected], e=state.effects?.[state.selected];
    if(node && e)selectTransform(node,'effect',state.selected,state.revision,{scale:e.Scale,offset:[e.X,e.Y,e.Z],rotation:[e.Pitch,e.Yaw,e.Roll]});
    if(firstEffects && effectRoot.children.length){firstEffects=false;frame();}
  } catch(e) { report('Effect preview: '+e); } finally { effectBusy=false; }
}
setInterval(pollEffects,350);
function layoutViewport() {
  const top=bar.offsetTop+bar.offsetHeight+12,bottom=hud.offsetHeight+24,height=Math.max(80,innerHeight-top-bottom);
  renderer.domElement.style.position='absolute';renderer.domElement.style.top=top+'px';
  document.getElementById('error').style.top=top+'px';camera.aspect=innerWidth/height;camera.updateProjectionMatrix();renderer.setSize(innerWidth,height);
}
const layoutObserver=new ResizeObserver(layoutViewport);layoutObserver.observe(bar);layoutObserver.observe(hud);layoutViewport();
addEventListener('resize',layoutViewport);
addEventListener('pagehide',()=>{gizmo.dispose();environment.dispose();textures.forEach(t=>t.dispose());controls.dispose();renderer.dispose();});
