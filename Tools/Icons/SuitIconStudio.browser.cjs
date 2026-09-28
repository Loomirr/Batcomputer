// Read-only input preview. Copies assets into artifacts; never changes suit projects or icons.
// NODE_PATH points to Playwright. Usage: node Tools/Icons/SuitIconStudio.browser.cjs <preview-folder>
const fs = require('fs'), path = require('path'), http = require('http'), assert = require('assert');
const { chromium } = require('playwright');
const rootDir = path.resolve(__dirname, '../..'), input = path.resolve(process.argv[2]);
const output = path.join(rootDir, 'artifacts/suit-icon-test'), preview = path.join(output, 'Preview');
fs.mkdirSync(preview, { recursive:true });
for (const entry of fs.readdirSync(input, { withFileTypes:true }))
  if (entry.isFile() && (/\.(glb|png|f32)$/.test(entry.name) || entry.name === 'models.js' || entry.name === 'animations.js'))
    fs.copyFileSync(path.join(input,entry.name),path.join(preview,entry.name));
for (const name of ['textures','export']) if (fs.existsSync(path.join(input,name))) fs.cpSync(path.join(input,name),path.join(preview,name),{recursive:true});
const source = fs.readFileSync(path.join(rootDir,'src/Batcomputer/Services/ModelPreviewService.cs'),'utf8');
fs.writeFileSync(path.join(preview,'index.html'),source.split('private const string ViewerHtml = """')[1].split('""";')[0].trim());
for (const name of ['three.min.js','GLTFLoader.js','OrbitControls.js','TransformControls.js','SkeletonUtils.js','GLTFExporter.js','CharacterExport.js','CharacterNormals.js','CharacterSurface.js','CharacterAssembly.js','CharacterMeshEditor.js','CharacterWorkshop.js','CharacterWorkshopShell.js','CharacterAnimationPreview.js','CharacterWorkshop.css','CharacterIconStudio.js','RectAreaLightUniformsLib.js'])
  fs.copyFileSync(path.join(rootDir,'Web/preview',name),path.join(preview,name));
const server = http.createServer((req,res) => {
  const file = path.resolve(preview,'.'+decodeURIComponent(req.url.split('?')[0]));
  if (!file.startsWith(preview+path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404);res.end();return; }
  res.setHeader('Content-Type', {'.js':'text/javascript','.css':'text/css','.html':'text/html','.png':'image/png','.glb':'model/gltf-binary'}[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(res);
});
(async () => {
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const browser = await chromium.launch({channel:'msedge',headless:true,args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
  try {
    const page = await browser.newPage({viewport:{width:1440,height:1000},acceptDownloads:true});
    await page.addInitScript(()=>{window.BATCOMPUTER_ICON_TEST_HOST=true;});
    const errors=[];page.on('pageerror',e=>errors.push(e.message));
    page.on('console',m=>{if(m.type()==='error' && /Shader Error|VALIDATE_STATUS|program not valid/.test(m.text())) errors.push(m.text());});
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(()=>!!window.characterIconStudio,{},{timeout:60000});
    await page.waitForFunction(()=>{let ready=true;root.traverse(n=>{for(const m of n.material?(Array.isArray(n.material)?n.material:[n.material]):[]) for(const t of Object.values(m)) if(t?.isTexture && (!t.image||t.image.complete===false||t.image.naturalWidth===0))ready=false;});return ready;});
    // Shader-only textures are not properties of MeshStandardMaterial; wait for those too.
    await page.waitForFunction(()=>materialEditorEntries.every(({material:m})=>{
      if(!m) return true;
      const shader={uniforms:{},vertexShader:'',fragmentShader:''};m.onBeforeCompile(shader,renderer);
      return Object.values(shader.uniforms).every(({value:t})=>!t?.isTexture ||
        (t.image && t.image.complete!==false && t.image.naturalWidth!==0));
    }));
    const surfaces = await page.evaluate(()=>{
      const body=models.find(m=>m.body).slots[0],cowl=models.find(m=>m.part==='Head'&&!m.hidden).slots[0];
      const entries=materialEditorEntries.filter(e=>e.material?.userData.eomSurface);
      const render=()=>characterIconStudio.render({preset:'suit'}).toDataURL();
      const before=render();
      entries.forEach(e=>{e.enabled.mmr=false;applyMaterialEditorEntry(e);});
      const disabled=render(),off=entries.every(e=>e.material.userData.eomSurface.enabled.value===0);
      entries.forEach(e=>{e.enabled.mmr=true;applyMaterialEditorEntry(e);});
      const restored=render();
      return {body,cowl,count:entries.length,changed:before!==disabled,restored:before===restored,off,
        linear:entries.every(e=>{
          const s={uniforms:{},vertexShader:'',fragmentShader:''};e.material.onBeforeCompile(s,renderer);
          return s.uniforms.bcSurfaceRao.value.encoding===THREE.LinearEncoding &&
            (!e.material.roughnessMap||e.material.roughnessMap.encoding===THREE.LinearEncoding);
        })};
    });
    assert(surfaces.body.material.includes('MI_Batman_ElectricLBM2_Body'));
    assert(surfaces.body.mmr && surfaces.body.rao && surfaces.body.ao,'Body requires MMR and structural RAO');
    assert(surfaces.cowl.rao && surfaces.cowl.ao && !surfaces.cowl.mmr,'Cowl must use its RAO, not a borrowed body MMR');
    assert(surfaces.count>=2 && surfaces.linear && surfaces.off && surfaces.changed && surfaces.restored,
      'Surface maps must be linear and toggling them must change/restore the actual render');
    const normalImpact = await page.evaluate(()=>{
      const entry=materialEditorEntries.find(e=>e.label?.startsWith('CharacterMesh0 -')&&e.material?.userData.structuralNormal);
      if(!entry)return null;
      const render=()=>{const canvas=characterIconStudio.render({preset:'suit'});
        return new Uint8ClampedArray(canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data);};
      const difference=(a,b)=>{let sum=0,changed=0;for(let i=0;i<a.length;i+=4){
        const delta=Math.abs(a[i]-b[i])+Math.abs(a[i+1]-b[i+1])+Math.abs(a[i+2]-b[i+2]);
        sum+=delta;if(delta>9)changed++;}return {meanRgbDelta:sum/(a.length/4)/3,changedPixels:changed};};
      const before=render(),changes={};
      for(const key of ['normal','legoNormal','microNormal']){
        entry.enabled[key]=false;applyMaterialEditorEntry(entry);
        changes[key]=difference(before,render());
        entry.enabled[key]=true;applyMaterialEditorEntry(entry);
      }
      return {changes,restored:difference(before,render()).meanRgbDelta===0,available:entry.available};
    });
    assert(normalImpact&&normalImpact.restored&&normalImpact.available.legoNormal&&
      normalImpact.available.microNormal&&Object.values(normalImpact.changes).every(value=>value.changedPixels>0),
      'Electric decal, LEGO and micro normals must each affect pixels and restore independently');
    fs.writeFileSync(path.join(output,'electric-normal-impact.json'),JSON.stringify(normalImpact,null,2));
    const closeNormalImpact=await page.evaluate(()=>{
      const entry=materialEditorEntries.find(e=>e.label?.startsWith('CharacterMesh0 -')&&e.material?.userData.structuralNormal);
      const position=camera.position.clone(),target=controls.target.clone();
      camera.position.set(.8,1.25,1.7);controls.target.set(0,1.05,0);controls.update();
      const screenshot=()=>{renderer.render(scene,camera);const gl=renderer.getContext(),buffer=new Uint8Array(gl.drawingBufferWidth*gl.drawingBufferHeight*4);
        gl.readPixels(0,0,gl.drawingBufferWidth,gl.drawingBufferHeight,gl.RGBA,gl.UNSIGNED_BYTE,buffer);return buffer;};
      const differs=(a,b)=>a.some((value,index)=>value!==b[index]);
      const before=screenshot(),changes={};
      for(const key of ['normal','legoNormal','microNormal']){
        entry.enabled[key]=false;applyMaterialEditorEntry(entry);
        changes[key]=differs(before,screenshot());
        entry.enabled[key]=true;applyMaterialEditorEntry(entry);
      }
      const restored=!differs(before,screenshot());
      camera.position.copy(position);controls.target.copy(target);controls.update();renderer.render(scene,camera);
      return {changes,restored};
    });
    assert(closeNormalImpact.restored&&Object.values(closeNormalImpact.changes).every(Boolean),
      'All three body normals must affect a close-up viewer render');
    const originalView=await page.evaluate(()=>({position:camera.position.toArray(),target:controls.target.toArray()}));
    await page.evaluate(()=>{camera.position.set(.8,1.25,1.7);controls.target.set(0,1.05,0);controls.update();renderer.render(scene,camera);});
    await page.screenshot({path:path.join(output,'electric-body-close.png')});
    await page.evaluate(({position,target})=>{camera.position.fromArray(position);controls.target.fromArray(target);controls.update();renderer.render(scene,camera);},originalView);
    const faceAndNormals = await page.evaluate(()=>{
      const body=models.find(m=>m.body).slots[0],face=models.find(m=>m.isface),
        first=Object.values(face?.poses?.Neutral||{})[0]||{},
        match=Object.keys(first).filter(name=>faceRig.bones.some(b=>b.name===name)).length,
        mouth=faceBandMats.find(f=>f.feature==='Mouth');
      const shaders={uniforms:{},vertexShader:'#include <uv_vertex>',fragmentShader:'#include <common>\n#include <map_fragment>\n#include <normal_fragment_maps>\n#include <roughnessmap_fragment>'};
      mouth?.mat.onBeforeCompile(shaders,renderer);
      const normal=materialEditorEntries.find(e=>e.material?.userData.structuralNormal)?.material;
      const normalShader={uniforms:{},vertexShader:'#include <uv_vertex>',fragmentShader:'#include <normal_fragment_maps>'};
      normal?.onBeforeCompile(normalShader,renderer);
      const initialFrame=Number(document.getElementById('frame')?.value||0);
      const neutral=characterIconStudio.render({preset:'menu'}).toDataURL();
      document.getElementById('expr').value='';applyExpression('',0);
      const bind=characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
      const neutralFromBind=characterIconStudio.render({preset:'menu'}).toDataURL();
      const stayedInBind=faceRig.bones.every(b=>{const p=faceRig.bind.get(b);
        return !p||b.position.equals(p.p)&&b.quaternion.equals(p.q)&&b.scale.equals(p.s);});
      document.getElementById('expr').value='Neutral';applyExpression('Neutral',initialFrame);
      const restored=characterIconStudio.render({preset:'menu'}).toDataURL();
      return {micro:body.micro,lego:body.nrm2,decal:body.nrm,microTile:body.microTile,
        microStrength:body.microStrength,poseSamples:Object.keys(face?.poses?.Neutral||{}).length,
        curveSamples:Object.keys(face?.curves?.Neutral||{}).length,matchedBones:match,
        mouthShaderSurvived:!!shaders.uniforms.faceTeethUMap,
        correctUvUnit:shaders.fragmentShader.includes('offset*0.01'),
        normalLayers:!!normalShader.uniforms.bcStructuralNormal&&!!normalShader.uniforms.bcMicroNormal,
        neutralDiffersFromBind:neutral!==bind,restored:neutral===restored,
        neutralFromBind:neutral===neutralFromBind,stayedInBind};
    });
    assert(faceAndNormals.micro && faceAndNormals.lego && faceAndNormals.decal &&
      faceAndNormals.micro!==faceAndNormals.lego && faceAndNormals.microTile===80 &&
      faceAndNormals.microStrength===0.1 && faceAndNormals.normalLayers,
      'Electric must sample authored, LEGO and tiled micro normals independently');
    assert(faceAndNormals.poseSamples>=5 && faceAndNormals.curveSamples>=5 &&
      faceAndNormals.matchedBones>=40 && faceAndNormals.mouthShaderSurvived &&
      faceAndNormals.correctUvUnit && faceAndNormals.neutralDiffersFromBind &&
      faceAndNormals.restored && faceAndNormals.neutralFromBind && faceAndNormals.stayedInBind,
      'Neutral face and mouth shader must survive first draw and icon rendering');
    fs.writeFileSync(path.join(output,'face-and-normal-checks.json'),JSON.stringify(faceAndNormals,null,2));
    const mouthToggleImpact=await page.evaluate(()=>{
      const pose='Dazed',keys=Object.keys(faceRig.poses?.[pose]||{}).map(Number).sort((a,b)=>a-b);
      if(!keys.length)return {error:'Dazed pose unavailable'};
      applyExpression(pose,Math.floor(keys.length/2));
      const render=()=>characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
      const states=faceBandMats.filter(b=>b.mouth);
      const before=render(),result={count:states.length,states:states.map(b=>({feature:b.feature,visible:b.mat.visible,
        uniforms:!!b.mouth.uniforms,enabled:{...b.mouth.enabled}})),changes:{},uiChanges:{}};
      for(const key of ['rim','teethU','teethD','tongue']){
        for(const b of states){b.mouth.enabled[key]=false;setMouthLayerUniforms(b.mouth,b.mouth.curves);}
        result.changes[key]={changed:render()!==before,
          uniforms:states.map(b=>b.mouth.uniforms?.['face'+({rim:'MouthRim',teethU:'TeethU',teethD:'TeethD',tongue:'Tongue'}[key])+'Enabled']?.value)};
        for(const b of states){b.mouth.enabled[key]=true;setMouthLayerUniforms(b.mouth,b.mouth.curves);}
      }
      result.restored=render()===before;
      for(const label of ['Mouth line / rim','Upper teeth','Lower teeth','Tongue']){
        const row=[...document.querySelectorAll('#facelayers label')].find(node=>node.textContent.trim()===label);
        const checkbox=row?.querySelector('input');
        if(!checkbox){result.uiChanges[label]='missing';continue;}
        checkbox.click();result.uiChanges[label]=render()!==before&&checkbox.checked===false;
        checkbox.click();result.uiChanges[label]&&=render()===before&&checkbox.checked===true;
      }
      applyExpression('Neutral',Math.floor(Object.keys(faceRig.poses.Neutral||{}).length/2));
      return result;
    });
    fs.writeFileSync(path.join(output,'mouth-toggle-impact.json'),JSON.stringify(mouthToggleImpact,null,2));
    assert(mouthToggleImpact.count===1&&mouthToggleImpact.restored&&
      Object.values(mouthToggleImpact.changes).every(value=>value.changed)&&
      Object.values(mouthToggleImpact.uiChanges).length===4&&Object.values(mouthToggleImpact.uiChanges).every(Boolean),
      'Every mouth-detail checkbox must change the Dazed face render and restore it when re-enabled');
    fs.writeFileSync(path.join(output,'material-checks.json'),JSON.stringify(surfaces,null,2));
    await page.screenshot({path:path.join(output,'electric-viewer.png')});
    const studioLights=await page.evaluate(()=>{const result=[];scene.traverse(n=>{if(n.isLight)result.push([n.intensity,n.position.toArray()]);});return result;});
    await page.getByRole('button',{name:'Scene',exact:true}).click();
    await page.getByLabel('Preview lighting').selectOption('Surface detail');
    const inspectionLights=await page.evaluate(()=>{const result=[];scene.traverse(n=>{if(n.isLight)result.push([n.intensity,n.position.toArray()]);});return result;});
    assert(inspectionLights[1][1][0]===.8 && inspectionLights[1][1][2]===8 &&
      inspectionLights[0][0]<studioLights[0][0],'Surface detail must provide grazing light and reduce ambient');
    await page.screenshot({path:path.join(output,'electric-surface-detail.png')});
    await page.getByRole('button',{name:'Reset lighting',exact:true}).click();
    const resetLights=await page.evaluate(()=>{const result=[];scene.traverse(n=>{if(n.isLight)result.push([n.intensity,n.position.toArray()]);});return result;});
    assert.deepEqual(resetLights,studioLights,'Studio lights must restore exactly');
    const state = () => page.evaluate(()=>JSON.stringify({pos:root.position.toArray(),parts:root.children.map(n=>[n.uuid,n.visible,n.matrix.toArray()]),camera:camera.matrix.toArray(),exposure:renderer.toneMappingExposure}));
    const before = await state();
    await page.getByRole('button',{name:'Icon studio',exact:true}).click();
    await page.waitForSelector('#suit-icon-studio canvas');
    const pngs = {};
    for(const preset of ['menu','left','right','suit']) {
      await page.getByLabel('Icon layout',{exact:true}).selectOption(preset);
      const size = preset==='suit'?256:512;
      const stats = await page.evaluate(()=>{const c=document.querySelector('#suit-icon-studio canvas');const data=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let clear=0,opaque=0;for(let i=3;i<data.length;i+=4){if(data[i]===0)clear++;if(data[i]>200)opaque++;}return {width:c.width,height:c.height,clear,opaque};});
      assert.equal(stats.width,size);assert.equal(stats.height,size);assert(stats.opaque>size*size*.1,'Icon must contain visible geometry');
      const previewSize=await page.evaluate(()=>{const rect=document.querySelector('#suit-icon-studio canvas').getBoundingClientRect();return {width:rect.width,height:rect.height};});
      if(preset==='suit') {
        assert(previewSize.width>size*1.5,'Suit tile should fill the main preview by default');
        await page.screenshot({path:path.join(output,'icon-studio-suit-fit.png')});
        await page.getByRole('button',{name:'Actual pixels',exact:true}).click();
        const actualSize=await page.evaluate(()=>{const rect=document.querySelector('#suit-icon-studio canvas').getBoundingClientRect();return {width:rect.width,height:rect.height};});
        assert(actualSize.width<=size+.5&&actualSize.height<=size+.5,'Actual-pixels view must not upscale the PNG');
        await page.screenshot({path:path.join(output,'icon-studio-suit-actual.png')});
        await page.getByRole('button',{name:'Fit preview',exact:true}).click();
      }
      if(preset!=='suit') assert(stats.clear>size*size*.01,'Portrait needs transparent margin');
      const wait = page.waitForEvent('download');await page.getByRole('button',{name:'Save PNG…',exact:true}).click();
      const download = await wait;assert.equal(download.suggestedFilename(),`Suit-icon-${preset}-${size}.png`);
      const file=path.join(output,`Electric-${preset}-${size}.png`);await download.saveAs(file);pngs[preset]=fs.readFileSync(file);
      assert.equal(pngs[preset].toString('ascii',1,4),'PNG');
      if(preset==='suit') {
        await page.evaluate(()=>{window.chrome=window.chrome||{};
          window.chrome.webview={postMessage:message=>{window.__iconTestMessage=message;}};});
        await page.getByRole('button',{name:'Test suit icon cook',exact:true}).click();
        const request=await page.evaluate(()=>window.__iconTestMessage);
        const expectedLayout=await page.evaluate(()=>window.PREVIEW_LAYOUT_KEY);
        assert(request?.type==='test-suit-icon'&&request.layout===expectedLayout,
          'In-app test must target the viewed saved suit');
        const imageComparison=await page.evaluate(async()=>{
          const image=new Image();image.src=window.__iconTestMessage.png;await image.decode();
          const source=document.querySelector('#suit-icon-studio canvas');
          if(image.width!==source.width||image.height!==source.height)return {sameSize:false};
          const copy=document.createElement('canvas');copy.width=image.width;copy.height=image.height;
          copy.getContext('2d').drawImage(image,0,0);
          const a=source.getContext('2d').getImageData(0,0,source.width,source.height).data;
          const b=copy.getContext('2d').getImageData(0,0,copy.width,copy.height).data;
          let changed=0,total=0,max=0;for(let i=0;i<a.length;i++){
            const delta=Math.abs(a[i]-b[i]);if(delta)changed++;total+=delta;max=Math.max(max,delta);}
          return {sameSize:true,changed,mean:total/a.length,max};
        });
        assert(imageComparison.sameSize&&imageComparison.mean<1,
          'In-app test must send the same rendered pixels shown by the studio: '+JSON.stringify(imageComparison));
        await page.evaluate(()=>characterIconStudio.testResult({success:true,message:'Dry run passed; icons unchanged.'}));
        assert.match(await page.locator('#suit-icon-studio .icon-status').textContent(),/icons unchanged/);
        await page.getByRole('button',{name:'Use as suit icon',exact:true}).click();
        const applyRequest=await page.evaluate(()=>window.__iconTestMessage);
        assert(applyRequest?.type==='apply-suit-icon'&&applyRequest.layout===expectedLayout,
          'Assignment request must target the viewed saved suit');
        await page.evaluate(()=>characterIconStudio.applyResult({success:false,message:'Assignment cancelled; project unchanged.'}));
        assert.match(await page.locator('#suit-icon-studio .icon-status').textContent(),/project unchanged/);
      }
    }
    assert(!pngs.left.equals(pngs.right),'Opposite views must be rendered, not reused');
    assert(!pngs.menu.equals(pngs.right) && !pngs.menu.equals(pngs.left),'Menu must be a separate front-facing view');
    // Same assembled mesh in Blender provides a lighting/framing comparison against the source template.
    const glbWait = page.waitForEvent('download');
    await page.evaluate(() => window.BatcomputerCharacterExport.download(THREE, root,
      new Map(root.children.map((object,i)=>[object,!models[i]?.hidden && !models[i]?.beside]))));
    await (await glbWait).saveAs(path.join(output,'Electric-reference.glb'));
    await page.getByLabel('Icon layout',{exact:true}).selectOption('menu');
    await page.screenshot({path:path.join(output,'icon-studio-wide.png')});
    await page.getByLabel('Icon zoom',{exact:true}).fill('0.85');
    await page.getByLabel('Icon exposure',{exact:true}).fill('0.5');
    await page.getByRole('button',{name:'Reset framing',exact:true}).click();
    assert.equal(await page.getByLabel('Icon zoom',{exact:true}).inputValue(),'1');
    assert.equal(await page.getByLabel('Icon exposure',{exact:true}).inputValue(),'0');
    await page.setViewportSize({width:540,height:800});
    await page.screenshot({path:path.join(output,'icon-studio-narrow.png')});
    assert(await page.evaluate(()=>document.querySelector('#suit-icon-studio').scrollWidth<=document.querySelector('#suit-icon-studio').clientWidth));
    await page.getByRole('button',{name:'Close icon studio'}).click();
    await page.setViewportSize({width:1440,height:1000});
    // Camera projection changes on resize, but transforms and project assembly must not change.
    assert.equal(await state(),before,'Rendering must not change the live character or camera');
    await page.getByRole('button',{name:'Icon studio',exact:true}).click();
    await page.waitForSelector('#suit-icon-studio canvas');
    await page.getByRole('button',{name:'Close icon studio'}).click();
    const isolation = await page.evaluate(()=>{
      const first=characterIconStudio.render().toDataURL();
      const visible=root.children.map(n=>n.visible);root.children.forEach(n=>n.visible=false);
      const second=characterIconStudio.render().toDataURL();root.children.forEach((n,i)=>n.visible=visible[i]);
      characterIconStudio.dispose();return first===second;
    });assert(isolation,'Preview hide/isolation must not drop parts from icons');
    assert(await page.evaluate(()=>{
      try { window.BatcomputerCharacterIconStudio({THREE,root,loaded:[],complete:false}).render(); return false; }
      catch(error) { return /failed to load/.test(error.message); }
    }),'An incomplete assembly must not export a partial icon');
    assert(await page.evaluate(()=>{
      try { characterIconStudio.render({zoom:NaN}); return false; }
      catch(error) { return /supported range/.test(error.message); }
    }),'Invalid framing must be rejected');
    assert.deepEqual(errors,[]);
    console.log('PASS: Electric material provenance, linear MMR/RAO, surface toggle/render restoration; four PNG exports, alpha, sizes, separate facing views, reset, responsive dialog, reopen, isolation and live-scene preservation.');
  } finally {await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
