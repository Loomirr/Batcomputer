// Headless regression of repo-owned preview assets; never operates the user's open browser.
const fs=require('fs'),path=require('path'),http=require('http'),assert=require('assert');
const {chromium}=require('playwright');
const folder=path.resolve(process.argv[2]);
const server=http.createServer((request,response)=>{
  const file=path.resolve(folder,'.'+decodeURIComponent(request.url.split('?')[0]));
  if(!file.startsWith(folder+path.sep)||!fs.existsSync(file)||!fs.statSync(file).isFile()){response.writeHead(404);response.end();return;}
  response.setHeader('Content-Type',{'.js':'text/javascript','.css':'text/css','.html':'text/html','.json':'application/json','.png':'image/png','.glb':'model/gltf-binary'}[path.extname(file)]||'application/octet-stream');fs.createReadStream(file).pipe(response);
});
(async()=>{
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  const browser=await chromium.launch({channel:'msedge',headless:true,args:['--use-angle=swiftshader','--enable-unsafe-swiftshader']});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:1000},acceptDownloads:true});const errors=[];page.on('pageerror',error=>errors.push(error.message));page.on('dialog',dialog=>dialog.accept());
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(()=>!!window.characterAnimationCreator,null,{timeout:60000});
    await page.getByRole('button',{name:'Motion',exact:true}).click();
    const name=await page.evaluate(()=>{
      const clip=PREVIEW_CHARACTER_ANIMATIONS.clips.find(value=>(value.targetPart||'CharacterMesh0')==='CharacterMesh0'&&value.tracks?.length);
      if(!clip)throw new Error('Fixture needs a sampled CharacterMesh0 clip.');
      const picker=document.querySelector('#cw-animations select[aria-label="Base-game animation"]');picker.value=clip.package;picker.onchange();return clip.package;
    });
    await page.getByRole('button',{name:'Create',exact:true}).click();
    await page.locator('#cw-animation-creator .from-motion').click();
    assert((await page.locator('#cw-animation-creator .status').textContent()).startsWith('Loaded '),'Selected native motion must become editable body keys');
    const initialKeys=await page.locator('.cw-key-marker').count();assert(initialKeys>10,'Imported motion must have real editable samples');
    await page.locator('#cw-animation-creator .add-window').click();
    await page.getByRole('textbox',{name:'Hit window label',exact:true}).fill('Right claw contact');await page.getByRole('textbox',{name:'Hit window label',exact:true}).press('Tab');
    await page.getByRole('combobox',{name:'Hit window hand',exact:true}).selectOption('both');
    await page.getByRole('spinbutton',{name:'Hit window end frame',exact:true}).fill('12');await page.getByRole('spinbutton',{name:'Hit window end frame',exact:true}).press('Tab');
    await page.getByRole('spinbutton',{name:'Hit window hit frame',exact:true}).fill('9');await page.getByRole('spinbutton',{name:'Hit window hit frame',exact:true}).press('Tab');
    await page.getByRole('spinbutton',{name:'Hit window start frame',exact:true}).fill('5');await page.getByRole('spinbutton',{name:'Hit window start frame',exact:true}).press('Tab');
    await page.evaluate(()=>{const timeline=document.querySelector('#cw-animation-creator .timeline');timeline.value='9';timeline.oninput();});
    assert((await page.locator('.combat-state').textContent()).includes('CONTACT'),'Scrubbing must preview contact against the actual pose');
    assert(await page.locator('.cw-combat-marker').count()===3,'Each active window needs draggable start/contact/end handles');
    // One retimed marker must commit through the same path as numeric/playhead edits.
    const marker=page.locator('.cw-combat-marker.hit'),bounds=await marker.boundingBox();assert(bounds);
    await page.mouse.move(bounds.x+bounds.width/2,bounds.y+bounds.height/2);await page.mouse.down();await page.mouse.move(bounds.x+bounds.width/2-20,bounds.y+bounds.height/2,{steps:4});await page.mouse.up();
    const contact=Number(await page.getByRole('spinbutton',{name:'Hit window hit frame',exact:true}).inputValue());assert(contact>=5&&contact<=12,'Dragged contact must remain a validated timeline frame');
    const downloadPromise=page.waitForEvent('download');await page.locator('#cw-animation-creator .save').click();const download=await downloadPromise;
    const savedBytes=fs.readFileSync(await download.path());assert(savedBytes.length<=2000000,'Saved motion must remain reopenable/cookable');
    const draft=JSON.parse(savedBytes);assert(draft.sourceAnimationPackage===name&&draft.tracks.length>1);
    assert.deepStrictEqual(draft.combatTiming.windows[0],{id:draft.combatTiming.windows[0].id,name:'Right claw contact',start:5,hit:contact,end:12,hand:'both'});
    assert(draft.combatTiming.previewOnly===true,'Preview timings must not masquerade as gameplay damage');
    await page.locator('#cw-animation-creator .remove-window').click();assert(await page.locator('.cw-combat-marker').count()===0);
    await page.locator('#cw-animation-creator .undo').click();assert(await page.locator('.cw-combat-marker').count()===3,'Undo must restore hit windows');
    await page.locator('#cw-animation-creator .file').setInputFiles({name:'timed.animation-draft.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(draft))});
    await page.waitForFunction(()=>/^(Loaded |Could not open draft)/.test(document.querySelector('#cw-animation-creator .status').textContent));
    assert((await page.locator('#cw-animation-creator .status').textContent()).startsWith('Loaded '),await page.locator('#cw-animation-creator .status').textContent());
    assert(await page.locator('.cw-combat-marker').count()===3,'Saved timing metadata must reopen with the motion keys');
    const bad={...draft,combatTiming:{...draft.combatTiming,windows:[{...draft.combatTiming.windows[0],hit:900}]}};
    await page.locator('#cw-animation-creator .file').setInputFiles({name:'invalid.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(bad))});
    await page.waitForFunction(()=>document.querySelector('#cw-animation-creator .status').textContent.startsWith('Could not open draft'));
    assert(await page.locator('.cw-combat-marker').count()===3&&await page.locator('.cw-key-marker').count()===initialKeys,'Invalid import must preserve the existing motion and timing');
    await page.screenshot({path:path.join(folder,'combat-timing.png')});assert(!errors.length,errors.join('\n'));
    fs.writeFileSync(path.join(folder,'combat-roundtrip.animation-draft.json'),savedBytes);
    console.log('PASS selected-body motion sampling/editing, frame/contact preview, drag retiming, timing undo, saved draft roundtrip and invalid-input preservation.');
  }finally{await browser.close();server.close();}
})().catch(error=>{console.error(error);process.exitCode=1;server.close();});
