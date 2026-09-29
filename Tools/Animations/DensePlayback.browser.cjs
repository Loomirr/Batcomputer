// Headless regression of repo-owned fixtures. Never controls the user's open browser.
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert');
const {chromium}=require('playwright');
const folder=path.resolve(process.argv[2]), draftPath=path.resolve(process.argv[3]);
const baseline=process.argv.includes('--baseline');
const web=path.resolve('Web/preview');
const server=http.createServer((request,response)=>{
 const relative=decodeURIComponent(request.url.split('?')[0]);
 let file=path.resolve(folder,'.'+relative);
 if(!file.startsWith(folder+path.sep)){response.writeHead(404);response.end();return;}
 const current=path.join(web,path.basename(file));
 if(!baseline&&relative==='/'+path.basename(file)&&fs.existsSync(current))file=current;
 if(relative==='/drafts/test.json')file=draftPath;
 if(!fs.existsSync(file)||!fs.statSync(file).isFile()){response.writeHead(404);response.end();return;}
 response.setHeader('Content-Type',{'.js':'text/javascript','.css':'text/css','.html':'text/html','.json':'application/json','.png':'image/png','.glb':'model/gltf-binary'}[path.extname(file)]||'application/octet-stream');
 // A populated real viewer can have its own manifest; keep this controlled fixture isolated.
 if(relative==='/models.js'){response.end(fs.readFileSync(file,'utf8')+'\nwindow.PREVIEW_USER_ANIMATIONS=window.__testAnimationLibrary;');return;}
 fs.createReadStream(file).pipe(response);
});
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({channel:'msedge',headless:true,args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
 try{
  const draft=JSON.parse(fs.readFileSync(draftPath,'utf8')), id='a'.repeat(32);
  const page=await browser.newPage({viewport:{width:1440,height:1000},acceptDownloads:true});const errors=[];
  page.on('pageerror',error=>errors.push(error.message));page.on('dialog',dialog=>dialog.accept());
  await page.addInitScript(({draft,id})=>{window.BATCOMPUTER_ANIMATION_LIBRARY_HOST=true;window.__libraryRequests=[];
   window.chrome??={};window.chrome.webview={postMessage(message){window.__libraryRequests.push(message);}};
   window.__testAnimationLibrary=[{id,name:draft.name,rigSignature:draft.rigSignature,character:'Test character',durationFrames:draft.durationFrames,status:'Cooked',usedBy:['Standing idle'],packages:['/Game/Mods/Test/A_Idle'],file:'drafts/test.json',available:true}];}, {draft,id});
  await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
  await page.waitForFunction(()=>!!window.characterAnimationCreator,null,{timeout:60000});
  await page.getByRole('button',{name:'Create',exact:true}).click();
  if(baseline)await page.locator('#cw-animation-creator .file').setInputFiles(draftPath);
  else {
   await page.getByRole('combobox',{name:'Your animation filter',exact:true}).selectOption('used');
   assert((await page.locator('.library-info').textContent()).includes('Standing idle'));
   await page.locator('.library-open').click();
  }
  await page.waitForFunction(()=>document.querySelector('#cw-animation-creator .status').textContent.startsWith('Loaded '));
  // Allow the initial resize/load observation to settle before measuring playback.
  await page.waitForTimeout(250);
  const result=await page.evaluate(()=>{
   const creator=window.characterAnimationCreator, panel=document.querySelector('#cw-animation-creator'), reel=document.querySelector('.cw-creator-reel');
   const firstKey=panel.querySelector('.keys button'), firstDiamond=reel.querySelector('.cw-key-marker');
   const observer=new MutationObserver(()=>{});observer.observe(panel.querySelector('.keys'),{childList:true,subtree:true});observer.observe(reel,{childList:true,subtree:true});
   const realNow=performance.now.bind(performance); let clock=realNow(); const begin=realNow();
   Object.defineProperty(performance,'now',{configurable:true,value:()=>clock});
   try { panel.querySelector('.play').click(); for(let i=0;i<120;i++){clock+=1000/60;creator.update();} panel.querySelector('.play').click(); }
   finally {delete performance.now;}
   const records=observer.takeRecords();observer.disconnect();
   return {elapsedMs:realNow()-begin,childMutations:records.length,stableKey:firstKey===panel.querySelector('.keys button'),stableDiamond:firstDiamond===reel.querySelector('.cw-key-marker'),keys:reel.querySelectorAll('.cw-key-marker').length};
  });
  console.log(JSON.stringify({mode:baseline?'baseline':'current',...result}));
  const expected=draft.tracks.reduce((count,track)=>count+track.keys.length,0);assert(result.keys===expected,'All source samples must remain editable');
  if(!baseline){assert(result.stableKey&&result.stableDiamond,'Playback must reuse timeline/key DOM');assert(result.childMutations===0,'Playback must not rebuild key rows/buttons');
   await page.evaluate(()=>{const timeline=document.querySelector('#cw-animation-creator .timeline');timeline.value='17';timeline.oninput();});
   await page.locator('#cw-animation-creator .key').click();await page.locator('#cw-animation-creator .undo').click();
   const downloadPromise=page.waitForEvent('download');await page.locator('#cw-animation-creator .save').click();const download=await downloadPromise;
   const saved=JSON.parse(fs.readFileSync(await download.path(),'utf8'));assert(saved.tracks.length===draft.tracks.length);
   for(const track of draft.tracks){const roundtrip=saved.tracks.find(item=>item.bone===track.bone);assert.deepStrictEqual(roundtrip.keys.map(key=>key.frame),track.keys.map(key=>key.frame));}
   await page.keyboard.press('Control+s');
   const request=await page.evaluate(()=>window.__libraryRequests.find(message=>message.type==='save-animation-draft'));
   assert(request&&request.id===id&&request.draft.tracks.length===draft.tracks.length&&request.layoutKey,'Library save must retain identity and exact viewer character context');
   // A delayed host acknowledgement may not erase edits made while the save was running.
   await page.locator('#cw-animation-creator .key').click();
   await page.evaluate(id=>window.characterAnimationCreator.librarySaved(id,window.PREVIEW_USER_ANIMATIONS,null),id);
   assert((await page.locator('.draft-indicator').textContent())==='Unsaved draft','Save acknowledgement must preserve newer unsaved edits');
   await page.screenshot({path:path.join(folder,'your-animations-dense-playback.png')});
  }
  assert(!errors.length,errors.join('\n'));console.log('PASS dense playback, complete key retention'+(baseline?' (baseline measurement)':' and library preview/filter/edit/undo/export'));
 }finally{await browser.close();server.close();}
})().catch(error=>{console.error(error);process.exitCode=1;server.close();});
