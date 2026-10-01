// Headless regression of local animation-studio fixtures, never the user's open browser.
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert');
const {chromium}=require('playwright');
const folder=path.resolve(process.argv[2]),blenderFile=process.argv[3]&&path.resolve(process.argv[3]);
const web=path.resolve('Web/preview');
const server=http.createServer((request,response)=>{
 const relative=decodeURIComponent(request.url.split('?')[0]);let file=path.resolve(folder,'.'+relative);
 if(!file.startsWith(folder+path.sep)){response.writeHead(404);response.end();return;}
 const current=path.join(web,path.basename(file));if(relative==='/'+path.basename(file)&&fs.existsSync(current))file=current;
 if(relative==='/blender-motion.glb'&&blenderFile)file=blenderFile;
 if(!fs.existsSync(file)||!fs.statSync(file).isFile()){response.writeHead(404);response.end();return;}
 response.setHeader('Content-Type',{'.js':'text/javascript','.css':'text/css','.html':'text/html','.glb':'model/gltf-binary'}[path.extname(file)]||'application/octet-stream');
 if(relative==='/models.js'){response.end(fs.readFileSync(file,'utf8')+'\nwindow.PREVIEW_CAN_SAVE_PLACEMENTS=true;window.PREVIEW_LAYOUT_KEY="suit:regression";');return;}
 fs.createReadStream(file).pipe(response);
});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({channel:'msedge',headless:true,args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:1050},acceptDownloads:true}),errors=[];
  page.on('pageerror',error=>errors.push(error.message));page.on('dialog',dialog=>dialog.accept());
  await page.addInitScript(()=>{window.BATCOMPUTER_ANIMATION_LIBRARY_HOST=true;window.__sourceRequests=[];window.chrome??={};
   window.chrome.webview={postMessage(message){window.__sourceRequests.push(message);
    if(message.type==='preview-character-animation')setTimeout(()=>{
     const primary=window.PREVIEW_CHARACTER_ANIMATIONS.clips.find(clip=>clip.package===message.package);
     window.characterAnimationPreview.bundleResult(message.package,primary?{primary,companions:[],warnings:[]}:null,primary?null:'Fixture clip unavailable');
    },0);
   }};});
  await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
  await page.waitForFunction(()=>!!window.characterAnimationCreator,null,{timeout:60000});
  await page.getByRole('button',{name:'Create',exact:true}).click();
  const panel=page.locator('#cw-animation-creator');
  const bone=name=>panel.locator('.cw-creator-bone-list').getByRole('button',{name,exact:true});
  await bone('Shoulder_L').click();await bone('Shoulder_R').click({modifiers:['Control']});
  assert((await panel.locator('.selection-info').textContent()).startsWith('2 selected'));
  await panel.locator('input[data-kind="rotation"][data-axis="0"]').fill('12');
  await panel.locator('input[data-kind="rotation"][data-axis="0"]').press('Tab');
  assert(await page.locator('.cw-creator-lane.selected').count()===2,'Both selected tracks must be highlighted');
  await panel.locator('.group-name').fill('Arms');await panel.locator('.group-create').click();
  assert((await panel.locator('.bone-group').textContent()).includes('Arms · 2 bones'));
  const save=async()=>{const waiting=page.waitForEvent('download');await panel.locator('.save').click();return JSON.parse(fs.readFileSync(await (await waiting).path(),'utf8'));};
  let draft=await save();assert(draft.tracks.length===2&&draft.boneGroups.groups[0].bones.length===2);
  assert(Math.abs(draft.tracks[0].keys[0].q[0]-draft.tracks[1].keys[0].q[0])<1e-8,'Numeric delta must apply to both selected bones');
  await panel.locator('.group-delete').click();await panel.locator('.undo').click();
  assert((await panel.locator('.bone-group').textContent()).includes('Arms · 2 bones'),'Undo restores group membership');
  await panel.getByRole('searchbox',{name:'Find animation bone'}).fill('Head');
  assert((await panel.locator('.selection-info').textContent()).startsWith('2 selected'),'Filtering must not silently change selection');
  await panel.getByRole('searchbox',{name:'Find animation bone'}).fill('');
  await panel.locator('.group-select').click();await panel.locator('.reset-bone').click();await panel.locator('.undo').click();
  draft=await save();assert(draft.tracks.every(track=>Math.abs(track.keys[0].q[0])>.01),'Bulk rest reset must undo atomically');
  // Parent + child selection must translate once, not once per selected ancestor.
  await panel.locator('.new').click();await bone('Chest').click();await bone('Clavicle_L').click({modifiers:['Control']});
  await page.evaluate(()=>{const g=window.characterAnimationCreator.gizmo;g.dispatchEvent({type:'dragging-changed',value:true});g.object.position.x+=.1;g.object.updateMatrixWorld(true);g.dispatchEvent({type:'objectChange'});g.dispatchEvent({type:'dragging-changed',value:false});});
  draft=await save();assert(Math.abs(draft.tracks.find(track=>track.bone==='Chest').keys[0].p[0]-.1)<1e-6);
  assert(draft.tracks.find(track=>track.bone==='Clavicle_L').keys[0].p.every(v=>Math.abs(v)<1e-7),'Selected child must not receive its parent delta twice');
  await panel.locator('.undo').click();draft=await save();assert(!draft.tracks.length,'Group gizmo edit must undo atomically');
  // Copy a real preloaded native clip through the asynchronous host-result path.
  const nativePackage=await page.evaluate(()=>window.PREVIEW_CHARACTER_ANIMATIONS.clips.find(clip=>(!clip.targetPart||clip.targetPart==='CharacterMesh0')&&clip.frameCount>3).package);
  await panel.locator('.source-picker').selectOption(nativePackage);await panel.locator('.source-edit').click();
  await page.waitForFunction(()=>document.querySelector('#cw-animation-creator .status').textContent.startsWith('Loaded '));
  draft=await save();assert(draft.sourceAnimationPackage===nativePackage&&draft.tracks.length&&draft.durationFrames>=3,'Native source must become editable motion without replacing its source');
  await panel.locator('.new').click();
  // Exercise safe host routing and library catalog update without navigating the viewer.
  await panel.locator('.source-pak').click();
  const request=await page.evaluate(()=>window.__sourceRequests.at(-1));
  assert(request.type==='import-animation-source'&&request.kind==='pak'&&request.layoutKey&&request.rigSignature);
  await page.evaluate(()=>window.characterAnimationCreator.sourceImported({catalog:[{name:'A_TestIdle',group:'Your cooked animations',package:'/Game/Mods/Test/A_TestIdle',kind:'Sequence'}]},null));
  assert((await panel.locator('.source-picker').textContent()).includes('A_TestIdle'));
  if(blenderFile){
   await panel.locator('.source-blender').click();
   await page.evaluate(()=>window.characterAnimationCreator.sourceImported({file:'blender-motion.glb',name:'Native shoulder test'},null));
   const status=await panel.locator('.status').textContent();assert(status.startsWith('Loaded '),status);
   draft=await save();console.log(JSON.stringify({duration:draft.durationFrames,tracks:draft.tracks.map(track=>({bone:track.bone,keys:track.keys.length}))}));assert(draft.durationFrames===30&&draft.tracks.length&&draft.tracks.every(track=>track.keys.length===31),'Blender motion must retain all 30 fps samples');
   assert(draft.tracks.some(track=>Math.abs(track.keys[15].q[0])+Math.abs(track.keys[15].q[1])+Math.abs(track.keys[15].q[2])>.03),'Animated pose must differ from rest');
   assert(draft.tracks.every(track=>Math.abs(Math.abs(track.keys.at(-1).q[3])-1)<1e-6),'Blender last frame must not wrap to a different pose');
   fs.writeFileSync(path.join(folder,'blender-roundtrip.animation-draft.json'),JSON.stringify(draft));
  }
  await page.screenshot({path:path.join(folder,'bone-groups-imports.png')});
  assert(!errors.length,errors.join('\n'));console.log('PASS multi-bone edits, parent-safe gizmo, atomic undo, groups, source import routing'+(blenderFile?' and Blender native-rig roundtrip':''));
 }finally{await browser.close();server.close();}
})().catch(error=>{console.error(error);process.exitCode=1;server.close();});
