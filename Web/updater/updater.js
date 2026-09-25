(() => {
 'use strict';
 const $=id=>document.getElementById(id), host=window.chrome?.webview;
 let latest={}, paused=false, reduced=matchMedia('(prefers-reduced-motion: reduce)'), renderer, scene, camera, model, mixer, clip, frameId;
 let time=0,last=0,lit={value:.18},clock={value:0},animating=false, framingBox, environment, framingPoints=[], indicators=[];
 try{paused=localStorage.getItem('pauseUpdaterAnimation')==='true';}catch{}
 const post=(action,value)=>host?.postMessage({action,value});
 function apply(s){
  latest=s;for(const id of ['headline','target','status','installed','notes','activity'])if(typeof s[id]==='string')$(id).textContent=s[id];
  $('sandbox').hidden=!s.sandbox;$('primary').textContent=s.action||'Check for updates';$('primary').disabled=!!s.disabled;
  $('cancel').hidden=!s.busy;$('channel').disabled=!!s.busy||!!s.scheduled;$('channel').value=s.beta?'beta':'stable';$('startup').checked=!!s.startup;
  $('restart').hidden=!(s.ready||s.scheduled);$('restart').disabled=!!s.busy;
  $('progressArea').hidden=!(s.busy||s.ready||s.scheduled);
  // This is actual byte progress, never an invented percent for verification.
  const m=(s.status||'').match(/Downloading\s+([\d.]+)\s*\/\s*([\d.]+)\s*MB/i);
  if(m&&+m[2]>0){const percent=Math.min(100,+m[1]/+m[2]*100);$('progress').value=percent;$('progressText').textContent=m[1]+' / '+m[2]+' MB · '+Math.floor(percent)+'%';}
  else if(s.ready||s.scheduled){$('progress').value=100;$('progressText').textContent='Download verified';}
  else{$('progress').removeAttribute('value');$('progressText').textContent=s.busy?(s.step===2?'Verifying files…':'Please wait…'):'';}
  if(s.error)$('activitySection').open=true;
  if(s.accent&&/^#[0-9a-f]{6}$/i.test(s.accent))document.documentElement.style.setProperty('--accent',s.accent);
  draw();
 }
 if(host)host.addEventListener('message',e=>apply(e.data));
 $('primary').onclick=()=>post('primary');$('cancel').onclick=()=>post('cancel');$('channel').onchange=()=>post('channel',$('channel').value==='beta');$('startup').onchange=()=>post('startup',$('startup').checked);
 $('releases').onclick=()=>post('releases');$('recovery').onclick=()=>post('recovery');$('restart').onclick=()=>post('restart');
 function motionLabel(){$('motion').textContent=reduced.matches?'Reduced motion':paused?'Play animation':'Pause animation';$('motion').disabled=reduced.matches;$('motion').setAttribute('aria-pressed',String(!paused&&!reduced.matches));}
 $('motion').onclick=()=>{paused=!paused;try{localStorage.setItem('pauseUpdaterAnimation',String(paused));}catch{}motionLabel();wake();};motionLabel();
 function animate(now){frameId=0;if(!animating||document.hidden||paused||reduced.matches)return;
  frameId=requestAnimationFrame(animate);if(now-last<1000/30)return;const dt=Math.min((now-last)/1000,.075);last=now;time+=dt;
  if(model){model.position.y=Math.sin(time*1.25)*.055;model.rotation.y=Math.sin(time*.37)*.09;}
  if(mixer)mixer.update(dt*(latest.busy ? .8 : .22));clock.value=time;lit.value=latest.busy ? 1.1 : latest.ready ? .7 : .2;
  indicators.forEach((m,i)=>m.emissiveIntensity=(latest.busy?.65:.15)*(.7+.3*Math.sin(time*2.4+i*.8)));draw();
 }
 function wake(){if(frameId)cancelAnimationFrame(frameId);frameId=0;last=performance.now();draw();if(animating&&!document.hidden&&!paused&&!reduced.matches)frameId=requestAnimationFrame(animate);}
 function draw(){if(renderer&&scene&&camera)renderer.render(scene,camera);}
 document.addEventListener('visibilitychange',wake);reduced.addEventListener('change',()=>{motionLabel();wake();});
 window.addEventListener('pagehide',()=>{animating=false;if(frameId)cancelAnimationFrame(frameId);if(renderer){environment?.dispose();scene.traverse(o=>{o.geometry?.dispose();for(const m of (Array.isArray(o.material)?o.material:[o.material]))if(m){m.map?.dispose();m.dispose();}});renderer.dispose();renderer.forceContextLoss();}});
 function fail(error){console.warn('Updater model unavailable:',error?.message||'WebGL context lost');animating=false;if(frameId)cancelAnimationFrame(frameId);$('model').hidden=true;$('poster').hidden=false;$('motion').hidden=true;post('modelUnavailable');}
 try{
  renderer=new THREE.WebGLRenderer({canvas:$('model'),alpha:true,antialias:true,powerPreference:'low-power'});renderer.setPixelRatio(Math.min(devicePixelRatio||1,1.5));renderer.outputEncoding=THREE.sRGBEncoding;renderer.toneMapping=THREE.ACESFilmicToneMapping;renderer.toneMappingExposure=.9;
  renderer.shadowMap.enabled=true;renderer.shadowMap.type=THREE.PCFSoftShadowMap;
  scene=new THREE.Scene();camera=new THREE.PerspectiveCamera(33,1,.05,100);camera.position.set(4.2,2.6,-6.9);camera.lookAt(0,0,0);
  // Offline studio softboxes supply broad highlights on molded plastic and the metal lever.
  const studio=new THREE.Scene();studio.background=new THREE.Color(.12,.14,.18);
  for(const [p,size,power] of [[[3,5,-4],[4,5],3],[[-4,2,-1],[2,4],1.2],[[0,4,5],[4,2],2]]){
   const panel=new THREE.Mesh(new THREE.PlaneGeometry(...size),new THREE.MeshBasicMaterial({color:new THREE.Color(power,power,power),side:THREE.DoubleSide}));panel.position.set(...p);panel.lookAt(0,0,0);studio.add(panel);
  }
  const pmrem=new THREE.PMREMGenerator(renderer);environment=pmrem.fromScene(studio,.025,.1,30);scene.environment=environment.texture;pmrem.dispose();studio.traverse(o=>{o.geometry?.dispose();o.material?.dispose();});
  scene.add(new THREE.HemisphereLight(0xd7e6ff,0x242731,.65));
  for(const [pos,color,power] of [[[3,5,-4],0xfff3e4,1.35],[[-4,3,-2],0xadc8ff,.35],[[1,5,4],0xa3ddd8,.8]]){const l=new THREE.DirectionalLight(color,power);l.position.set(...pos);if(power===1.35){l.castShadow=true;l.shadow.mapSize.set(1024,1024);Object.assign(l.shadow.camera,{left:-4,right:4,top:4,bottom:-4,near:.1,far:20});l.shadow.bias=-.0004;l.shadow.normalBias=.02;l.shadow.radius=3;}scene.add(l);}
  function fitCamera(){
   if(!framingBox)return;
   const direction=new THREE.Vector3(4.2,2.6,-6.9).normalize(),right=new THREE.Vector3().crossVectors(new THREE.Vector3(0,1,0),direction).normalize(),up=new THREE.Vector3().crossVectors(direction,right);
   const tan=Math.tan(THREE.MathUtils.degToRad(camera.fov/2));let distance=0;
   // Fit the animation envelope in view space, with room for hover/yaw and the control below.
   for(const original of framingPoints)for(const yaw of [-.09,.09]){
    const p=original.clone().applyAxisAngle(new THREE.Vector3(0,1,0),yaw);distance=Math.max(distance,(Math.abs(p.dot(right))+.08)/(tan*camera.aspect*.88)+p.dot(direction),(Math.abs(p.dot(up))+.08)/(tan*.88)+p.dot(direction));
   }
   camera.position.copy(direction.multiplyScalar(distance));camera.lookAt(0,0,0);camera.updateMatrixWorld();
  }
  const resize=()=>{const b=$('stage').getBoundingClientRect();renderer.setSize(b.width,b.height,false);camera.aspect=b.width/Math.max(b.height,1);camera.updateProjectionMatrix();fitCamera();draw();};new ResizeObserver(resize).observe($('stage'));resize();
  $('model').addEventListener('webglcontextlost',e=>{e.preventDefault();fail();});
  new THREE.GLTFLoader().load('Batcomputer.glb',g=>{
   // Center the complete asset, including its lever, without changing animation targets.
   model=new THREE.Group();framingBox=new THREE.Box3().setFromObject(g.scene);
   function sampleBounds(){g.scene.updateMatrixWorld(true);framingBox.union(new THREE.Box3().setFromObject(g.scene));g.scene.traverse(o=>{if(!o.isMesh)return;o.geometry.computeBoundingBox();const b=o.geometry.boundingBox;for(const x of [b.min.x,b.max.x])for(const y of [b.min.y,b.max.y])for(const z of [b.min.z,b.max.z])framingPoints.push(new THREE.Vector3(x,y,z).applyMatrix4(o.matrixWorld));});}
   if(g.animations.length){mixer=new THREE.AnimationMixer(g.scene);clip=mixer.clipAction(g.animations[0]);clip.play();for(let i=0;i<=24;i++){mixer.setTime(g.animations[0].duration*i/24);sampleBounds();}mixer.setTime(0);}else sampleBounds();
   const center=framingBox.getCenter(new THREE.Vector3());
   g.scene.position.sub(center);model.add(g.scene);scene.add(model);
   framingPoints.forEach(p=>p.sub(center));framingBox.translate(center.clone().negate());fitCamera();
   const floor=new THREE.Mesh(new THREE.PlaneGeometry(20,20),new THREE.ShadowMaterial({opacity:.25}));floor.rotation.x=-Math.PI/2;floor.position.y=framingBox.min.y-.13;floor.receiveShadow=true;scene.add(floor);
   model.traverse(o=>{if(!o.isMesh)return;o.castShadow=true;o.receiveShadow=true;const materials=Array.isArray(o.material)?o.material:[o.material];for(const m of materials){m.envMapIntensity=.55;if(m.name==='Studio_Batcomputer'){m.color.setRGB(.40,.47,.58);m.metalness=.08;}if(m.name.startsWith('Indicator_'))indicators.push(m);if(m.map)m.map.anisotropy=Math.min(8,renderer.capabilities.getMaxAnisotropy());
    if(m.map){m.onBeforeCompile=shader=>{shader.uniforms.bcClock=clock;shader.uniforms.bcActivity=lit;shader.fragmentShader='uniform float bcClock; uniform float bcActivity;\n'+shader.fragmentShader;
      shader.fragmentShader=shader.fragmentShader.replace('#include <emissivemap_fragment>',`#include <emissivemap_fragment>
      #ifdef USE_MAP
       vec3 ink=texture2D(map,vUv).rgb;
       float hi=max(max(ink.r,ink.g),ink.b),lo=min(min(ink.r,ink.g),ink.b);
       float mask=smoothstep(.25,.55,hi-lo)*smoothstep(.35,.75,hi);
       float pulse=.55+.45*sin(bcClock*3.0+floor(vUv.y*16.0)*1.4);
       totalEmissiveRadiance+=ink*mask*bcActivity*pulse;
      #endif`);};m.customProgramCacheKey=()=> 'batcomputer-button-lights-v1';m.needsUpdate=true;}
   }});
   $('poster').hidden=true;$('model').hidden=false;animating=true;wake();post('modelReady');
   // Geometry-only QA hook for the standalone authored-page test, never the app bridge.
   if(!host)window.updaterFrameBounds=(seconds,yaw)=>{
    const savedTime=mixer?.time||0,savedY=model.position.y,savedYaw=model.rotation.y;
    mixer?.setTime(seconds);model.position.y=.055;model.rotation.y=yaw;scene.updateMatrixWorld(true);
    let maxX=0,maxY=0;model.traverse(o=>{if(!o.isMesh)return;const b=o.geometry.boundingBox;for(const x of [b.min.x,b.max.x])for(const y of [b.min.y,b.max.y])for(const z of [b.min.z,b.max.z]){const p=new THREE.Vector3(x,y,z).applyMatrix4(o.matrixWorld).project(camera);maxX=Math.max(maxX,Math.abs(p.x));maxY=Math.max(maxY,Math.abs(p.y));}});
    mixer?.setTime(savedTime);model.position.y=savedY;model.rotation.y=savedYaw;return{maxX,maxY};
   };
  },undefined,fail);
 }catch(error){fail(error);}
 post('ready');
 // Standalone browser preview is explicitly simulated; the app never enables these controls.
 if(!host){$('demo').hidden=false;const base={installed:'1.0 Beta 2 · DESIGN PREVIEW',target:'1.0 Beta 2 → 1.0 Beta 3 · 71.7 MB',notes:'Animated Batcomputer, simpler update controls, and workshop fixes.',activity:'Preview only. No files are downloaded or installed.',beta:true};
  const states={available:{headline:'A new version is ready.',status:'Download now. Install when you’re ready.',action:'Download update'},download:{headline:'Downloading your update…',status:'Downloading 45.9 / 71.7 MB',busy:true,disabled:true,step:1,action:'Downloading…'},verify:{headline:'Checking every file',status:'Verifying your download…',busy:true,disabled:true,step:2,action:'Verifying…'},ready:{headline:'Ready to install.',status:'Save your work, then close Batcomputer to update.',ready:true,action:'Install when I exit'},complete:{headline:'You’re up to date.',installed:'1.0 Beta 3 · DESIGN PREVIEW',status:'Startup confirmed. Your previous version is available in recovery.',action:'Check for updates'}};
  document.querySelectorAll('[data-demo]').forEach(b=>b.onclick=()=>apply({...base,...states[b.dataset.demo]}));apply({...base,...states.download});
  $('primary').onclick=()=>apply({...base,...states.ready});$('cancel').onclick=()=>apply({...base,...states.available});
 }
})();
