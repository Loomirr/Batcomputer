// Read-only smoke test for a headless --preview-character-output folder.
// NODE_PATH must include Playwright; no suit project, icon, pak or game files are changed.
const fs = require('fs'), path = require('path'), http = require('http'), assert = require('assert');
const { chromium } = require('playwright');
const folder = path.resolve(process.argv[2]);
if (!fs.existsSync(path.join(folder, 'index.html'))) throw new Error('Pass a generated character preview folder.');
const server = http.createServer((request, response) => {
  const file = path.resolve(folder, '.' + decodeURIComponent(request.url.split('?')[0]));
  if (!file.startsWith(folder + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    response.writeHead(404); response.end(); return;
  }
  response.setHeader('Content-Type', { '.js':'text/javascript', '.css':'text/css', '.html':'text/html', '.png':'image/png', '.glb':'model/gltf-binary' }[path.extname(file)] || 'application/octet-stream');
  fs.createReadStream(file).pipe(response);
});
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const browser = await chromium.launch({ channel:'msedge', headless:true, args:['--use-angle=swiftshader','--enable-unsafe-swiftshader'] });
  try {
    const page = await browser.newPage({ viewport:{ width:1440, height:900 } });
    await page.addInitScript(() => {
      window.__previewMessages = [];
      window.chrome ??= {};
      window.chrome.webview = { postMessage(message) { window.__previewMessages.push(message); } };
    });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => !!window.characterIconStudio, null, { timeout:60000 });
    await page.getByRole('button', { name:'Motion', exact:true }).click();
    const startup = await page.evaluate(() => ({
      selected:document.querySelector('#cw-animations select[aria-label="Base-game animation"]').value,
      playDisabled:document.querySelector('#cw-animations .play').disabled,
      messages:window.__previewMessages.length,
      parts:models.map((entry, i) => ({ part:entry.part, beside:entry.beside, visible:root.children[i].visible }))
    }));
    assert(startup.selected === '' && startup.playDisabled && startup.messages === 0,
      'Opening Motion must leave the character at Rest without requesting Idle');
    assert(startup.parts.some(part => part.beside && part.visible),
      'Rest pose must keep authored-visible separate parts visible');
    await page.screenshot({ path:path.join(folder, 'rest-pose-startup.png') });
    const clips = await page.locator('#cw-animations select[aria-label="Base-game animation"] option').allTextContents();
    assert(clips.length >= 500 && clips.includes('A_Idle_Batman') && clips.some(name => name.startsWith('A_Walk')) &&
      clips.includes('AM_VaultOver1_Batman'), 'The full family catalog must include sequences and montages');
    await page.getByRole('searchbox', { name:'Search animations' }).fill('A_Jump_Batman');
    const filtered = await page.locator('#cw-animations select[aria-label="Base-game animation"] option').allTextContents();
    assert(filtered.length > 1 && filtered.length < clips.length && filtered.some(name => name === 'A_Jump_Batman'),
      'Family search must find clips beyond the initial movement set');
    await page.getByRole('searchbox', { name:'Search animations' }).fill('');
    const result = await page.evaluate(() => {
      const body = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')];
      const capture = () => { const values = []; body.traverse(node => {
        if (node.isBone) values.push(...node.position.toArray(), ...node.quaternion.toArray());
      }); return values; };
      const before = capture();
      window.__animationBefore = before;
      const summary = () => models.map((entry, i) => {
        const object = root.children[i], box = new THREE.Box3().setFromObject(object), center = box.getCenter(new THREE.Vector3());
        return { part:entry.part, anchor:entry.anchor, beside:entry.beside, visible:object.visible,
          center:center.toArray().map(value => Math.round(value*1000)/1000) };
      });
      const beforeParts = summary();
      const picker = document.querySelector('#cw-animations select[aria-label="Base-game animation"]');
      picker.value = window.PREVIEW_CHARACTER_ANIMATIONS.clips.find(clip => clip.label === 'Walk').package; picker.onchange();
      const scrub = document.querySelector('#cw-animations input[aria-label="Animation position"]');
      scrub.value = '500'; scrub.oninput();
      const after = capture();
      const afterParts = summary();
      const difference = (a,b) => a.reduce((total,value,i) => total + Math.abs(value-b[i]), 0);
      return { moved:difference(before,after), time:document.querySelector('#cw-animations .time').textContent,
        parts:beforeParts.map((entry,i) => ({ ...entry, after:afterParts[i].center, afterVisible:afterParts[i].visible,
          delta:afterParts[i].center.map((value,axis) => Math.round((value-entry.center[axis])*1000)/1000) })) };
    });
    await page.screenshot({ path:path.join(folder, 'walk-preview.png') });
    const iconRestored = await page.evaluate(() => {
      const body = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')];
      const capture = () => { const values = []; body.traverse(node => {
        if (node.isBone) values.push(...node.position.toArray(), ...node.quaternion.toArray());
      }); return values; };
      const posed = capture();
      const icon = characterIconStudio.render({ preset:'suit' });
      const after = capture();
      return { width:icon.width, difference:posed.reduce((sum,value,i) => sum + Math.abs(value-after[i]), 0) };
    });
    const restored = await page.evaluate(() => {
      const body = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')];
      const capture = () => { const values = []; body.traverse(node => {
        if (node.isBone) values.push(...node.position.toArray(), ...node.quaternion.toArray());
      }); return values; };
      document.querySelector('#cw-animations .rest').click();
      const atRest = capture();
      return atRest.reduce((total,value,i) => total + Math.abs(value-window.__animationBefore[i]), 0);
    });
    assert(result.moved > .01, 'Walk must visibly change the body skeleton');
    for (const part of result.parts.filter(part => ['__BareHead','Cape','Face','Head'].includes(part.part)))
      assert(Math.hypot(...part.delta) < .35, `${part.part} must not jump out of the centered character frame`);
    assert(result.parts.find(part => part.part === 'Torso')?.afterVisible === false,
      'Separate glider geometry must stay hidden during movement playback');
    assert(restored < .0001, 'Rest pose must restore every body bone');
    assert(await page.evaluate(() => root.children[models.findIndex(entry => entry.beside)]?.visible) === true,
      'Rest pose must restore separate display parts');
    assert(iconRestored.width === 256 && iconRestored.difference < .0001,
      'Icon rendering must restore the previewed body pose after its neutral capture');
    await page.locator('#cw-animations .play').click();
    await page.waitForFunction(() => Number(document.querySelector('#cw-animations input[aria-label="Animation position"]').value) > 0,
      null, { timeout:5000 });
    await page.locator('#cw-animations .play').click();
    await page.locator('#cw-animations .rest').click();
    const synchronized = await page.evaluate(async () => {
      window.chrome.webview = { postMessage() {} };
      const picker = document.querySelector('#cw-animations select[aria-label="Base-game animation"]');
      const scrub = document.querySelector('#cw-animations input[aria-label="Animation position"]');
      const results = [];
      for (const number of [0, 1, 2]) {
        const bundle = await (await fetch(`sample-animation-${number}.json`)).json();
        const parts = [bundle.primary, ...bundle.companions].map(clip => clip.targetPart);
        const capture = () => Object.fromEntries(parts.map(part => {
          const object = root.children[models.findIndex(entry => entry.part === part)];
          const values = []; object.traverse(node => {
            if (node.isBone) values.push(...node.position.toArray(), ...node.quaternion.toArray());
          }); return [part, values];
        }));
        const before = capture();
        const cape = root.children[models.findIndex(entry => entry.part === 'Cape')];
        const capeSceneBefore = cape && [...cape.position.toArray(), ...cape.quaternion.toArray(), ...cape.scale.toArray()];
        const faceState = () => faceBandMats.map(b => [b.mat.visible,
          b.mouth?.uniforms?.faceMouthHide?.value ?? null]);
        const faceBefore = JSON.stringify(faceState());
        picker.value = bundle.primary.package; picker.onchange();
        window.characterAnimationPreview.bundleResult(bundle.primary.package, bundle, null);
        scrub.value = '500'; scrub.oninput();
        const after = capture();
        const capeSceneAfter = cape && [...cape.position.toArray(), ...cape.quaternion.toArray(), ...cape.scale.toArray()];
        if (number === 0) window.__syncScreenshot = true;
        document.querySelector('#cw-animations .rest').click();
        const atRest = capture();
        results.push({ primary:bundle.primary.targetPart, montage:!!bundle.primary.sampledSequence,
          status:document.querySelector('#cw-animations .status').textContent,
          faceLayersRestored:JSON.stringify(faceState()) === faceBefore,
          capeSceneDelta:capeSceneBefore?.reduce((sum, value, i) => sum + Math.abs(value - capeSceneAfter[i]), 0) ?? null,
          parts:parts.map(part => ({ part,
            moved:after[part].reduce((sum, value, i) => sum + Math.abs(value - before[part][i]), 0),
            restored:atRest[part].reduce((sum, value, i) => sum + Math.abs(value - before[part][i]), 0) })) });
      }
      return results;
    });
    assert.deepStrictEqual(synchronized[0].parts.map(result => result.part), ['CharacterMesh0','Head','Face','Cape']);
    assert.deepStrictEqual(synchronized[1].parts.map(result => result.part), ['Face','CharacterMesh0','Head','Cape']);
    assert(synchronized[2].montage && synchronized[2].parts.some(result => result.part === 'Cape'),
      'A montage must preview its source sequence with matching cape movement');
    for (const bundle of synchronized)
    {
      assert(bundle.faceLayersRestored, 'Rest pose must restore face material visibility and mouth settings');
      if (bundle.parts.some(part => part.part === 'Cape'))
        assert(bundle.capeSceneDelta < .0001, 'A native cape track must not also inherit the Chest follower transform');
      for (const part of bundle.parts)
        assert(part.moved > .01 && part.restored < .0001,
          `${part.part} in a synchronized bundle must move and then restore exactly`);
    }
    await page.evaluate(async () => {
      const bundle = await (await fetch('sample-animation-0.json')).json();
      const picker = document.querySelector('#cw-animations select[aria-label="Base-game animation"]');
      picker.value = bundle.primary.package; picker.onchange();
      window.characterAnimationPreview.bundleResult(bundle.primary.package, bundle, null);
      const scrub = document.querySelector('#cw-animations input[aria-label="Animation position"]');
      scrub.value = '500'; scrub.oninput();
    });
    await page.screenshot({ path:path.join(folder, 'synchronized-jump-preview.png') });
    await page.getByRole('button', { name:'Create', exact:true }).click();
    const authored = await page.evaluate(() => {
      const body = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')];
      const picker = document.querySelector('#cw-animation-creator [aria-label="Animation bone"]');
      picker.value = [...picker.options].find(option => option.value === 'Spine_03')?.value || picker.options[1].value;
      picker.onchange();
      const bone = body.getObjectByName(picker.value), before = bone.quaternion.toArray();
      const timeline = document.querySelector('#cw-animation-creator .timeline'); timeline.value = '15'; timeline.oninput();
      const rotation = document.querySelector('#cw-animation-creator input[data-kind="rotation"][data-axis="0"]');
      rotation.value = '25'; rotation.onchange();
      const after = bone.quaternion.toArray();
      const icon = characterIconStudio.render({ preset:'suit' });
      const afterIcon = bone.quaternion.toArray();
      return { bone:picker.value, moved:after.reduce((sum, value, i) => sum + Math.abs(value-before[i]), 0),
        iconWidth:icon.width, iconRestored:after.every((value, i) => Math.abs(value-afterIcon[i]) < .0001),
        keyCount:document.querySelectorAll('#cw-animation-creator .keys button').length };
    });
    assert(authored.moved > .01 && authored.keyCount === 1 && authored.iconWidth === 256 && authored.iconRestored,
      'Creator must pose a native bone, set a key and restore the pose after icon rendering');
    const interpolation = await page.evaluate(name => {
      const bone = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')].getObjectByName(name);
      const timeline = document.querySelector('#cw-animation-creator .timeline');
      const key = bone.quaternion.clone();
      timeline.value = '0'; timeline.oninput(); const rest = bone.quaternion.clone();
      timeline.value = '7'; timeline.oninput(); const halfway = bone.quaternion.clone();
      timeline.value = '15'; timeline.oninput();
      return rest.angleTo(halfway) / rest.angleTo(key);
    }, authored.bone);
    assert(interpolation > .4 && interpolation < .55, 'Creator must interpolate bone rotations between Rest and the first key');
    await page.screenshot({ path:path.join(folder, 'animation-creator-draft.png') });
    const downloadPromise = page.waitForEvent('download');
    await page.locator('#cw-animation-creator .save').click();
    const draftDownload = await downloadPromise;
    const draft = JSON.parse(fs.readFileSync(await draftDownload.path(), 'utf8'));
    assert(draft.schema === 'batcomputer.animation-draft.v1' && draft.tracks[0].bone === authored.bone &&
      draft.tracks[0].keys[0].frame === 15, 'Draft export must retain the selected bone and exact frame');
    await page.getByRole('button', { name:'Motion', exact:true }).click();
    const leftAtRest = await page.evaluate(name => {
      const bone = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')].getObjectByName(name);
      return bone.quaternion.toArray();
    }, authored.bone);
    await page.getByRole('button', { name:'Create', exact:true }).click();
    const reopened = await page.evaluate(name => {
      const bone = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')].getObjectByName(name);
      return bone.quaternion.toArray();
    }, authored.bone);
    assert(reopened.some((value, i) => Math.abs(value-leftAtRest[i]) > .01),
      'Leaving Create must restore Rest, and reopening must reapply the draft at its timeline position');
    await page.locator('#cw-animation-creator .file').setInputFiles({
      name:'roundtrip.json', mimeType:'application/json', buffer:Buffer.from(JSON.stringify(draft)) });
    await page.waitForFunction(() => document.querySelector('#cw-animation-creator .status').textContent.startsWith('Loaded 1 bone track'));
    assert(await page.locator('#cw-animation-creator .keys button').count() === 1,
      'Saved draft must load into the matching native rig');
    await page.locator('#cw-animation-creator .file').setInputFiles({
      name:'wrong-rig.json', mimeType:'application/json', buffer:Buffer.from(JSON.stringify({ ...draft, rigSignature:'another rig' })) });
    await page.waitForFunction(() => document.querySelector('#cw-animation-creator .status').textContent.startsWith('Could not open draft'));
    assert(await page.locator('#cw-animation-creator .keys button').count() === 1,
      'A mismatched rig must be rejected without erasing the open draft');
    assert(!errors.length, errors.join('\n'));
    console.log('character animation preview: PASS', JSON.stringify({ catalogCount:clips.length,
      bodyMoved:result.moved, restored, iconRestored, synchronized, authored }));
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; server.close(); });
