// Isolated browser check for an already generated character preview.
// Usage: NODE_PATH=<playwright modules> node Tools/Icons/FaceParity.browser.cjs <preview> <name>
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert');
const {chromium}=require('playwright');
const input=path.resolve(process.argv[2]),name=process.argv[3]||'face';
const output=path.resolve(__dirname,'../../artifacts/material-accuracy/face-browser');
fs.mkdirSync(output,{recursive:true});
const server=http.createServer((req,res)=>{
  const file=path.resolve(input,'.'+decodeURIComponent(req.url.split('?')[0]));
  if(!file.startsWith(input+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile()){
    res.writeHead(404);res.end();return;
  }
  res.setHeader('Content-Type',{'.js':'text/javascript','.css':'text/css','.html':'text/html',
    '.png':'image/png','.glb':'model/gltf-binary'}[path.extname(file)]||'application/octet-stream');
  fs.createReadStream(file).pipe(res);
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({channel:'msedge',headless:true,
    args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:1000}}),errors=[];
    page.on('pageerror',e=>errors.push(e.message));
    page.on('console',m=>{if(m.type()==='error'&&/Shader Error|VALIDATE_STATUS|program not valid/.test(m.text()))errors.push(m.text());});
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(()=>faceRig?.bones?.length>0&&faceRig?.poses?.Neutral&&window.characterIconStudio,
      {},{timeout:60000});
    await page.waitForFunction(()=>{
      let ready=true;
      root.traverse(node=>{for(const material of node.material?(Array.isArray(node.material)?node.material:[node.material]):[])
        for(const texture of Object.values(material))
          if(texture?.isTexture&&(!texture.image||texture.image.complete===false||texture.image.naturalWidth===0))ready=false;});
      for(const band of faceBandMats){
        const shader={uniforms:{},vertexShader:'',fragmentShader:''};band.mat.onBeforeCompile(shader,renderer);
        for(const {value:texture} of Object.values(shader.uniforms))
          if(texture?.isTexture&&(!texture.image||texture.image.complete===false||texture.image.naturalWidth===0))ready=false;
      }
      return ready;
    },{},{timeout:60000});
    const poseOptions=await page.locator('#expr option').allTextContents();
    assert(poseOptions.includes('Neutral')&&poseOptions.includes('Closed')&&poseOptions.includes('Open')&&
      poseOptions.length>=16,'Native face poses should be offered in the selector');
    await page.getByRole('navigation',{name:'Inspector sections'}).getByRole('button',{name:'Face',exact:true}).click();
    const faceLayers=page.locator('#facelayers');
    await faceLayers.getByRole('checkbox',{name:'Upper teeth'}).waitFor();
    const layerOriginal=await page.evaluate(()=>characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL());
    await faceLayers.getByRole('checkbox',{name:'Upper teeth'}).uncheck();
    const layerHidden=await page.evaluate(()=>characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL());
    assert.notEqual(layerHidden,layerOriginal,'Upper-teeth toggle must change rendered pixels');
    await page.locator('#expr').selectOption('Closed');
    await page.locator('#expr').selectOption('Neutral');
    const afterPoseSwitch=await page.evaluate(()=>({enabled:faceBandMats.find(b=>b.mouth)?.mouth?.enabled.teethU,
      image:characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL()}));
    assert.equal(afterPoseSwitch.enabled,false,'Face layer switch must survive expression changes');
    assert.equal(afterPoseSwitch.image,layerHidden,'Returning to Neutral must retain the hidden teeth layer');
    await faceLayers.getByRole('button',{name:'Restore all face layers'}).click();
    const layerRestored=await page.evaluate(()=>characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL());
    assert.equal(layerRestored,layerOriginal,'Restoring face layers must restore the render');
    const editorVisibility=await page.evaluate(()=>{
      const entry=materialEditorEntries.find(e=>e.kind==='face'&&e.faceBand?.feature==='Mouth');
      if(!entry)return null;
      entry.enabled.base=false;applyMaterialEditorEntry(entry);
      applyExpression('Closed');applyExpression('Neutral');
      const stayedHidden=entry.faceBand.editorVisible===false&&entry.faceBand.mat.visible===false;
      resetMaterialEditorEntry(entry);
      return {stayedHidden,restored:entry.faceBand.mat.visible===true};
    });
    assert(editorVisibility?.stayedHidden&&editorVisibility.restored,
      'Surfaces face-layer visibility must survive pose changes and restore independently');
    await page.screenshot({path:path.join(output,`${name}-face-controls.png`)});
    await page.locator('#expr').selectOption('Closed');
    const closedVisibility=await page.evaluate(()=>({
      pose:document.getElementById('expr').value,
      upper:faceCurveVisible({feature:'EyelidUpperL'},faceRig.curves.Closed[Object.keys(faceRig.curves.Closed)[2]]),
      lower:faceCurveVisible({feature:'EyelidLowerL'},faceRig.curves.Closed[Object.keys(faceRig.curves.Closed)[2]]),
      image:characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL()
    }));
    assert(closedVisibility.pose==='Closed'&&!closedVisibility.upper&&closedVisibility.lower,
      'Closed pose curves must swap the eyelid layers');
    await page.locator('#expr').selectOption('Neutral');
    const neutralImage=await page.evaluate(()=>characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL());
    assert.notEqual(closedVisibility.image,neutralImage,'Selecting another pose must change the render');
    const result=await page.evaluate(()=>{
      const face=models.find(model=>model.isface);
      const first=Object.values(face.poses.Neutral)[0],
        matching=Object.keys(first).filter(bone=>faceRig.bones.some(b=>b.name===bone)).length;
      const eyeBoneDeltas=faceRig.bones.filter(b=>/Eye|Lid|Brow/i.test(b.name)).map(b=>{
        const p=faceRig.bind.get(b),sample=face.poses.Neutral[Object.keys(face.poses.Neutral)[2]]?.[b.name];
        return {name:b.name,bind:p?[...p.p.toArray(),...p.q.toArray(),...p.s.toArray()]:null,
          neutral:sample||null};
      });
      const mouth=faceBandMats.find(band=>band.feature==='Mouth');
      const eyeUvBounds=faceBandMats.filter(b=>['EyeL','EyeR','HeadLowerUnder'].includes(b.feature)).map(b=>{
        const group=b.mesh.geometry.groups.find(g=>g.materialIndex===b.slot),uv=b.mesh.geometry.attributes.uv,
          uv1=b.mesh.geometry.attributes.aUv1,index=b.mesh.geometry.index;
        const bounds=attr=>{let loU=Infinity,loV=Infinity,hiU=-Infinity,hiV=-Infinity;
          for(let i=group.start;i<group.start+group.count;i++){
            const v=index?index.getX(i):i,u=attr.getX(v),w=attr.getY(v);
            loU=Math.min(loU,u);hiU=Math.max(hiU,u);loV=Math.min(loV,w);hiV=Math.max(hiV,w);
          }return [loU,loV,hiU,hiV];};
        return {band:b.band,feature:b.feature,uv:bounds(uv),uv1:uv1?bounds(uv1):null};
      });
      const shader={uniforms:{},vertexShader:'#include <uv_vertex>',
        fragmentShader:'#include <common>\n#include <map_fragment>\n#include <normal_fragment_maps>'};
      mouth.mat.onBeforeCompile(shader,renderer);
      const skinningBefore=skinningFixed,materialBefore=mouth.mat;
      const initialFrame=Number(document.getElementById('frame')?.value||0);
      const neutralKey=Object.keys(face.poses.Neutral)[initialFrame];
      applyExpression('Neutral',initialFrame);applyFaceMaterialCurves(null);
      const neutralBonesOnly=characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
      applyExpression('',0);applyFaceMaterialCurves(face.curves.Neutral?.[neutralKey]||null);
      const neutralCurvesOnly=characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
      applyExpression('Neutral',initialFrame);
      const mouthLayers={};
      const mouthState=mouth.mat.userData.faceMouth;
      const originalMouthSurface={normal:mouth.mat.normalMap,roughness:mouth.mat.roughness,metalness:mouth.mat.metalness};
      mouth.mat.normalMap=null;mouth.mat.roughness=1;mouth.mat.metalness=0;mouth.mat.needsUpdate=true;
      mouthLayers.flatSurface=characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
      mouth.mat.normalMap=originalMouthSurface.normal;mouth.mat.roughness=originalMouthSurface.roughness;
      mouth.mat.metalness=originalMouthSurface.metalness;mouth.mat.needsUpdate=true;
      for(const uniform of ['faceTeethUMap','faceTeethDMap','faceTongueMap']){
        const original=mouthState.uniforms[uniform].value;
        mouthState.uniforms[uniform].value=faceTransparentTex;
        mouthLayers[uniform]=characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
        mouthState.uniforms[uniform].value=original;
      }
      const interiors=['faceTeethUMap','faceTeethDMap','faceTongueMap'];
      const originalInteriors=interiors.map(key=>mouthState.uniforms[key].value);
      interiors.forEach(key=>mouthState.uniforms[key].value=faceTransparentTex);
      mouthLayers.withoutInterior=characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL();
      interiors.forEach((key,index)=>mouthState.uniforms[key].value=originalInteriors[index]);
      const frames=Object.keys(face.poses.Neutral).map((key,index)=>{
        applyExpression('Neutral',index);
        return {key,png:characterIconStudio.render({preset:'menu',neutralFace:false}).toDataURL()};
      });
      applyExpression('Neutral',initialFrame);
      const neutral=characterIconStudio.render({preset:'menu'});
      const neutralPng=neutral.toDataURL(),neutralVisible=neutral.getContext('2d')
        .getImageData(0,0,neutral.width,neutral.height).data.some((value,index)=>index%4!==3&&value>10);
      const neutralPixels=neutral.getContext('2d').getImageData(0,0,neutral.width,neutral.height).data;
      const bandImpact=faceBandMats.filter(b=>b.mat.visible!==false).map(b=>{
        b.mat.visible=false;
        const canvas=characterIconStudio.render({preset:'menu',neutralFace:false}),
          pixels=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;
        b.mat.visible=true;
        let changed=0,minX=512,minY=512,maxX=0,maxY=0;
        for(let i=0;i<pixels.length;i+=4){
          if(Math.abs(pixels[i]-neutralPixels[i])+Math.abs(pixels[i+1]-neutralPixels[i+1])+
             Math.abs(pixels[i+2]-neutralPixels[i+2])<20)continue;
          const x=(i/4)%512,y=Math.floor((i/4)/512);changed++;
          minX=Math.min(minX,x);minY=Math.min(minY,y);maxX=Math.max(maxX,x);maxY=Math.max(maxY,y);
        }
        return {band:b.band,feature:b.feature,changed,bbox:changed?[minX,minY,maxX,maxY]:null,
          png:[8,13,14].includes(b.band)?canvas.toDataURL():null};
      });
      document.getElementById('expr').value='';
      applyExpression('',0);const bind=characterIconStudio.render({preset:'menu',neutralFace:false}),bindPng=bind.toDataURL();
      const bindPixels=bind.getContext('2d').getImageData(0,0,bind.width,bind.height).data;
      // Even from bind pose, the regular icon path must capture neutral and leave the viewport alone.
      const neutralFromBind=characterIconStudio.render({preset:'menu'}).toDataURL();
      const stayedInBind=faceRig.bones.every(b=>{const p=faceRig.bind.get(b);return !p||b.position.equals(p.p)&&b.quaternion.equals(p.q)&&b.scale.equals(p.s);});
      document.getElementById('expr').value='Neutral';
      applyExpression('Neutral',initialFrame);const restoredCanvas=characterIconStudio.render({preset:'menu'}),restored=restoredCanvas.toDataURL();
      const restoredPixels=restoredCanvas.getContext('2d').getImageData(0,0,restoredCanvas.width,restoredCanvas.height).data;
      let bindDifference=0,restoreDifference=0;
      for(let i=0;i<neutralPixels.length;i+=4){
        if(neutralPixels[i]!==bindPixels[i]||neutralPixels[i+1]!==bindPixels[i+1]||neutralPixels[i+2]!==bindPixels[i+2])bindDifference++;
        if(neutralPixels[i]!==restoredPixels[i]||neutralPixels[i+1]!==restoredPixels[i+1]||neutralPixels[i+2]!==restoredPixels[i+2])restoreDifference++;
      }
      return {faceMaterial:face.slots[0]?.material,bandCount:face.fbands.length,eyeUvBounds,eyeBoneDeltas,bandImpact,
        poseSamples:Object.keys(face.poses.Neutral).length,curveSamples:Object.keys(face.curves.Neutral||{}).length,
        matchedBones:matching,mouthShaderSurvived:!!shader.uniforms.faceTeethUMap,
        uvUnit:shader.fragmentShader.includes('offset*0.01'),neutralVisible,
        neutralDiffersFromBind:neutralPng!==bindPng,restored:neutralPng===restored,
        neutralFromBind:neutralPng===neutralFromBind,stayedInBind,initialFrame,
        bindDifference,restoreDifference,
        skinningBefore,skinningAfter:skinningFixed,materialReplaced:materialBefore!==mouth.mat,
        png:neutralPng,bindPng,restoredPng:restored,frames,neutralBonesOnly,neutralCurvesOnly,mouthLayers};
    });
    const png=Buffer.from(result.png.split(',')[1],'base64');delete result.png;
    fs.writeFileSync(path.join(output,`${name}-neutral.png`),png);
    fs.writeFileSync(path.join(output,`${name}-bind.png`),Buffer.from(result.bindPng.split(',')[1],'base64'));
    fs.writeFileSync(path.join(output,`${name}-restored.png`),Buffer.from(result.restoredPng.split(',')[1],'base64'));
    delete result.bindPng;delete result.restoredPng;
    for(const mode of ['neutralBonesOnly','neutralCurvesOnly']){
      fs.writeFileSync(path.join(output,`${name}-${mode}.png`),Buffer.from(result[mode].split(',')[1],'base64'));
      delete result[mode];
    }
    for(const [layer,png] of Object.entries(result.mouthLayers))
      fs.writeFileSync(path.join(output,`${name}-without-${layer}.png`),Buffer.from(png.split(',')[1],'base64'));
    result.mouthLayers=Object.keys(result.mouthLayers);
    for(const band of result.bandImpact){
      if(band.png)fs.writeFileSync(path.join(output,`${name}-without-band-${band.band}.png`),Buffer.from(band.png.split(',')[1],'base64'));
      delete band.png;
    }
    for(const frame of result.frames)
      fs.writeFileSync(path.join(output,`${name}-neutral-frame-${frame.key}.png`),Buffer.from(frame.png.split(',')[1],'base64'));
    result.frames=result.frames.map(frame=>frame.key);
    fs.writeFileSync(path.join(output,`${name}-checks.json`),JSON.stringify(result,null,2));
    assert(result.poseSamples>=5&&result.matchedBones>=40&&result.bandCount>=15);
    assert(result.mouthShaderSurvived&&result.uvUnit&&result.neutralVisible);
    assert(result.neutralDiffersFromBind&&result.restored&&result.neutralFromBind&&result.stayedInBind);
    const eyeLeft=result.eyeBoneDeltas.find(b=>b.name==='Eye_L')?.neutral,
      eyeRight=result.eyeBoneDeltas.find(b=>b.name==='Eye_R')?.neutral;
    assert(eyeLeft&&eyeRight&&eyeLeft[0]>.1&&eyeRight[0]>.1&&
      Math.abs(eyeLeft[1]-eyeRight[1])<.02&&eyeLeft[2]*eyeRight[2]<0,
      'Both eye tracks must stay on their matching bones after animation conversion');
    assert.deepEqual(errors,[]);
    console.log(`PASS: ${name} neutral face; ${result.matchedBones} matching bones, ${result.poseSamples} samples, mouth shader retained.`);
  }finally{await browser.close();server.close();}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});
