// Headless icon-studio checks against a generated local character preview.
// No projects or game installations are modified; assignment messages are captured only.
const fs = require('fs'), path = require('path'), http = require('http'), assert = require('assert');
const { chromium } = require('playwright');
const folder = path.resolve(process.argv[2]), output = path.resolve(process.argv[3] || folder);
const source = path.resolve(__dirname, '../../Web/preview');
const server = http.createServer((request, response) => {
  const rel = decodeURIComponent(request.url.split('?')[0]);
  const file = path.resolve(folder, '.' + rel);
  if (!file.startsWith(folder + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { response.writeHead(404); response.end(); return; }
  response.setHeader('Content-Type', { '.js':'text/javascript', '.css':'text/css', '.html':'text/html', '.png':'image/png', '.glb':'model/gltf-binary' }[path.extname(file)] || 'application/octet-stream');
  if (path.basename(file) === 'index.html') {
    let html = fs.readFileSync(file, 'utf8');
    html = html.replace('<head>', '<head><script>window.BATCOMPUTER_ICON_TEST_HOST=true;window.PREVIEW_CAN_SAVE_PLACEMENTS=true;window.PREVIEW_LAYOUT_KEY="icon-test";</script>');
    response.end(html);
  } else if (path.basename(file) === 'CharacterIconStudio.js') response.end(fs.readFileSync(path.join(source, 'CharacterIconStudio.js')));
  else fs.createReadStream(file).pipe(response);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel:'msedge', headless:true, args:['--use-angle=swiftshader','--enable-unsafe-swiftshader'] });
  try {
    fs.mkdirSync(output, {recursive:true});
    const page = await browser.newPage({ viewport:{width:1440,height:1000} });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.__iconMessages=[]; window.chrome ??= {};
      window.chrome.webview={postMessage(message) {window.__iconMessages.push(message);}};
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => !!window.characterIconStudio, null, {timeout:60000});
    await page.getByRole('button', {name:'Icon studio',exact:true}).click();
    await page.waitForSelector('#suit-icon-studio canvas');
    const cameras=await page.evaluate(()=>characterIconStudio.presets);
    assert(cameras.left.position[1]<0 && cameras.right.position[1]>0,'native left/right slot camera directions');
    assert(cameras.suit.position[2]<cameras.menu.position[2] && cameras.suit.distance<cameras.menu.distance,'suit tile uses its own keyed torso camera, not duplicated portrait');
    const keyed={suit:[1,[214.912338257,-.000003722,84.802169800]],left:[2,[234.269195557,-88.891006470,145.477584839]],menu:[3,[249.055755615,.000006280,145.477584839]],right:[4,[236.246292114,83.494705200,145.477584839]]};
    for(const [role,[frame,position]] of Object.entries(keyed)) {
      assert.strictEqual(cameras[role].frame,frame,role+' template keyframe');
      position.forEach((value,index)=>assert(Math.abs(cameras[role].position[index]-value)<.000001,role+' exact template camera position'));
      assert(Math.abs(cameras[role].lens-89.67384338378906)<.000001,role+' template lens');
    }
    assert(Math.abs(cameras.left.yaw)>19 && Math.abs(cameras.right.yaw)>19,'side presets use distinct keyed template angles');
    const horizontal=page.getByLabel('Horizontal framing',{exact:true});
    await horizontal.fill('0.12');await horizontal.dispatchEvent('input');
    await page.getByLabel('Icon layout',{exact:true}).selectOption('left');
    assert.strictEqual(await horizontal.inputValue(),'0','each slot starts with separate framing');
    await horizontal.fill('-0.08');await horizontal.dispatchEvent('input');
    await page.getByLabel('Icon layout',{exact:true}).selectOption('menu');
    assert.strictEqual(await horizontal.inputValue(),'0.12','camera adjustments survive switching slots');
    await page.locator('.icon-reset').click();
    if(process.argv.includes('--reference')) {
      const fixtureDownload=page.waitForEvent('download',{timeout:60000});
      await page.evaluate(async()=>{
        const snapshot=BatcomputerCharacterExport.snapshot(THREE,root,new Map());
        try {const binary=await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Fixture export timed out')),45000);try{new THREE.GLTFExporter().parse(snapshot.root,result=>{clearTimeout(timer);resolve(result);},{binary:true,onlyVisible:true,embedImages:true,maxTextureSize:512});}catch(e){clearTimeout(timer);reject(e);}});const link=document.createElement('a');link.href=URL.createObjectURL(new Blob([binary],{type:'model/gltf-binary'}));link.download='reference-assembly.glb';document.querySelector('#suit-icon-studio').appendChild(link);link.click();link.remove();}
        finally{snapshot.dispose();}
      });
      await (await fixtureDownload).saveAs(path.join(output,'reference-assembly.glb'));
    }
    const template=await page.evaluate(()=>characterIconStudio.templateSettings());
    assert.deepStrictEqual(Object.keys(template.cameras).sort(),['left','menu','right','suit']);
    await page.getByText('Share a studio template',{exact:true}).click();
    const downloaded=await Promise.all([page.waitForEvent('download',{timeout:15000}),page.locator('.icon-template-export').click()]).then(values=>values[0]).catch(async error=>{throw new Error(error.message+'\nStudio status: '+await page.locator('.icon-status').textContent()+'\nScript errors: '+JSON.stringify(errors));});
    assert(downloaded.suggestedFilename().endsWith('.icon-studio.json'),'template download has scoped filename');
    await downloaded.saveAs(path.join(output,'shared.icon-studio.json'));
    const fromDisk=JSON.parse(fs.readFileSync(path.join(output,'shared.icon-studio.json'),'utf8'));
    assert.deepStrictEqual(fromDisk,template,'exported JSON is exactly the reusable settings');
    fromDisk.name='Shared camera test';fromDisk.cameras.suit.lens=120;fromDisk.lighting.cool=1.2;fromDisk.lighting.key=.8;
    await page.locator('.icon-template-file').setInputFiles({name:'Shared.icon-studio.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(fromDisk))});
    await page.waitForFunction(()=>characterIconStudio.templateSettings().name==='Shared camera test');
    assert.deepStrictEqual(await page.evaluate(()=>characterIconStudio.templateSettings()),fromDisk,'JSON import round-trips all four cameras and lighting');
    const malformed=JSON.parse(JSON.stringify(fromDisk));malformed.cameras.right.zoom=90;
    await page.locator('.icon-template-file').setInputFiles({name:'Invalid.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(malformed))});
    await page.waitForFunction(()=>document.querySelector('.icon-status').textContent.startsWith('Template not changed:'));
    assert.deepStrictEqual(await page.evaluate(()=>characterIconStudio.templateSettings()),fromDisk,'bad template cannot partially apply');
    const legacy=JSON.parse(JSON.stringify(template));legacy.version=1;delete legacy.lighting.key;
    await page.evaluate(value=>characterIconStudio.importTemplate(value),legacy);
    assert.strictEqual((await page.evaluate(()=>characterIconStudio.templateSettings())).lighting.key,1,'older shared templates receive the neutral key-light default');
    await page.evaluate(value=>characterIconStudio.importTemplate(value),template);
    await page.screenshot({path:path.join(output,'icon-studio.png')});
    await page.getByRole('button',{name:'Create and assign all character icons',exact:true}).click();
    await page.waitForFunction(() => window.__iconMessages.some(m=>m.type==='apply-character-icons'),null,{timeout:60000});
    const message = await page.evaluate(() => window.__iconMessages.find(m=>m.type==='apply-character-icons'));
    assert.deepStrictEqual(Object.keys(message.icons).sort(), ['left','menu','right','suit']);
    for (const [role,data] of Object.entries(message.icons)) {
      const bytes=Buffer.from(data.split(',')[1],'base64'), size=role==='suit'?256:512;
      assert(bytes.readUInt32BE(16)===size && bytes.readUInt32BE(20)===size,role+' native size');
      fs.writeFileSync(path.join(output,role+'.png'),bytes);
    }
    assert(await page.locator('.icon-apply-all').isDisabled(),'prevent duplicate assignments while awaiting cook');
    assert(await horizontal.isDisabled(),'freeze camera settings during cooking');
    await page.evaluate(()=>characterIconStudio.applyResult({message:'Test saved'}));
    assert(await horizontal.isEnabled(),'camera controls recover after cooking');
    await page.getByLabel('Icon layout',{exact:true}).selectOption('left');
    await page.getByRole('button',{name:'Create and assign selected icon',exact:true}).click();
    await page.waitForFunction(()=>window.__iconMessages.filter(m=>m.type==='apply-character-icons').length===2,null,{timeout:60000});
    const single=await page.evaluate(()=>window.__iconMessages.filter(m=>m.type==='apply-character-icons')[1]);
    assert.deepStrictEqual(Object.keys(single.icons),['left']);
    await page.evaluate(()=>characterIconStudio.applyResult({message:'Test saved'}));
    const lighting=await page.evaluate(()=>{
      const sample=(warm,cool,key=1)=>{const canvas=characterIconStudio.render({preset:'menu',warm,cool,key});return {png:canvas.toDataURL(),pixels:canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data};};
      const normal=sample(1,1),noWarm=sample(0,1),noBlue=sample(1,0),noKey=sample(1,1,0),weakWarm=sample(.05,1);
      const difference=(a,b)=>{let total=0,count=0;for(let i=0;i<a.pixels.length;i+=4){if(!a.pixels[i+3])continue;for(let c=0;c<3;c++)total+=Math.abs(a.pixels[i+c]-b.pixels[i+c]);count+=3;}return total/count;};
      return {normal:normal.png,noWarm:noWarm.png,noBlue:noBlue.png,noKey:noKey.png,weakWarm:weakWarm.png,blueDelta:difference(normal,noBlue),warmDelta:difference(normal,noWarm),keyDelta:difference(normal,noKey),weakWarmDelta:difference(weakWarm,noWarm)};
    });
    assert(lighting.normal!==lighting.noWarm && lighting.normal!==lighting.noBlue && lighting.normal!==lighting.noKey,'both rims and the white key affect the render');
    assert(lighting.blueDelta>lighting.warmDelta*.1 && lighting.keyDelta>1,'blue and white lighting remain visible beside the warm rim');
    assert(lighting.weakWarmDelta<lighting.blueDelta*.25,'warm strength 0.05 must be subtle, not overwhelm blue lighting');
    for(const role of ['noWarm','noBlue','noKey','weakWarm'])fs.writeFileSync(path.join(output,'lighting-'+role+'.png'),Buffer.from(lighting[role].split(',')[1],'base64'));
    fs.writeFileSync(path.join(output,'lighting-balance.json'),JSON.stringify({blue:lighting.blueDelta,warm:lighting.warmDelta,key:lighting.keyDelta,warmAt005:lighting.weakWarmDelta},null,2));
    assert.deepStrictEqual(errors,[]);
    for(const viewport of [{width:1000,height:700},{width:650,height:700}]) {
      await page.setViewportSize(viewport);
      const clipped=await page.evaluate(()=>{
        const d=document.querySelector('#suit-icon-studio'),r=d.getBoundingClientRect();
        const footer=d.querySelector('.icon-actions').getBoundingClientRect();
        return r.right>innerWidth || r.bottom>innerHeight || (innerWidth>700 && footer.bottom>r.bottom);
      });
      assert(!clipped,'studio and footer remain within viewport');
      await page.screenshot({path:path.join(output,`icon-studio-${viewport.width}.png`)});
    }
    await page.locator('header button[aria-label="Close icon studio"]').click();
    await page.getByRole('button',{name:'Icon studio',exact:true}).click();
    await page.waitForSelector('#suit-icon-studio .icon-preview canvas');
    assert(await horizontal.isEnabled(),'close/reopen renderer lifecycle');
    console.log('PASS: all four keyed template cameras, independent framing, JSON export/import + atomic invalid-file rejection, all four native sizes, single assignment, busy controls, blue/warm lights, compact layout and renderer lifecycle.');
  } finally { await browser.close(); server.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;server.close();});
