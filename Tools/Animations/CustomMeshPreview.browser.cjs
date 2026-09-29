// Headless read-only preview probe; never controls a user's open browser.
const fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('playwright');
const folder=path.resolve(process.argv[2]),part=process.argv[3];
const server=http.createServer((request,response)=>{
 if(request.url==='/favicon.ico'){response.writeHead(204);response.end();return;}
 const file=path.resolve(folder,'.'+decodeURIComponent(request.url.split('?')[0]));
 if(!file.startsWith(folder+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile()){
  response.writeHead(404);response.end();return;
 }
 response.setHeader('Content-Type',{'.js':'text/javascript','.css':'text/css','.html':'text/html','.json':'application/json','.png':'image/png','.glb':'model/gltf-binary','.f32':'application/octet-stream'}[path.extname(file)]||'application/octet-stream');
 fs.createReadStream(file).pipe(response);
});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({channel:'msedge',headless:true,args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
 try{
  const page=await browser.newPage({viewport:{width:1280,height:820}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
  await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
  await page.waitForFunction(name=>typeof partStates!=='undefined'&&partStates.has(name),part,{timeout:60000});
  const result=await page.evaluate(name=>{
   const state=partStates.get(name),meshes=[];
   state.scene.traverse(o=>{if(!o.isMesh)return;
    const uv=o.geometry.attributes.uv,materials=Array.isArray(o.material)?o.material:[o.material];
    let uvBounds=null;
    if(uv){let u0=Infinity,u1=-Infinity,v0=Infinity,v1=-Infinity;
     for(let i=0;i<uv.count;i++){const u=uv.getX(i),v=uv.getY(i);u0=Math.min(u0,u);u1=Math.max(u1,u);v0=Math.min(v0,v);v1=Math.max(v1,v);}
     uvBounds=[u0,u1,v0,v1];}
    meshes.push({name:o.name,vertices:o.geometry.attributes.position?.count,uvBounds,
     materials:materials.map(m=>({name:m.name,color:m.color?.getHexString(),map:!!m.map,mapImage:m.map?.image?.currentSrc||m.map?.image?.src||null,mapWidth:m.map?.image?.width||null,side:m.side,roughness:m.roughness,metalness:m.metalness}))});
   });return {part:name,uvChannel:state.uvChannel,meshes};
  },part);
  console.log(JSON.stringify({result,errors},null,2));
  await page.evaluate(name=>{const b=new THREE.Box3().setFromObject(partStates.get(name).scene),c=b.getCenter(new THREE.Vector3()),s=b.getSize(new THREE.Vector3()).length();camera.position.copy(c).add(new THREE.Vector3(s*1.6,s*.3,s*.7));controls.target.copy(c);controls.update();renderer.render(scene,camera);},part);
  await page.screenshot({path:path.join(folder,'custom-mesh-probe.png')});
  if(errors.length)throw new Error(errors.join('\n'));
  if(!result.meshes.length||result.meshes.some(m=>!m.uvBounds||m.materials.some(mat=>!mat.mapWidth)))throw new Error('Custom mesh texture/UV preview is incomplete');
  const twins=await page.evaluate(name=>{let count=0;partStates.get(name).scene.traverse(o=>{if(!o.isMesh)return;
   const idx=o.geometry.index;if(!idx)return;
   for(let i=0;i+5<idx.count;i+=3)if(idx.getX(i)===idx.getX(i+3)&&idx.getX(i+1)===idx.getX(i+5)&&idx.getX(i+2)===idx.getX(i+4))count++;
  });return count;},part);
  if(twins)throw new Error('Preview still contains coplanar reverse triangle twins: '+twins);
  console.log('PASS textured custom mesh preview has no reverse triangle twins');
 }finally{await browser.close();server.close();}
})().catch(error=>{console.error(error);process.exitCode=1;server.close();});
