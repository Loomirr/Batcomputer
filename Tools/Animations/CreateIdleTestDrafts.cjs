// Generate and preview three small native-body animation drafts from the current viewer rig.
// Usage: NODE_PATH=<playwright modules> node CreateIdleTestDrafts.cjs <preview folder> <output folder>
const fs = require('fs'), path = require('path'), http = require('http');
const { chromium } = require('playwright');
const preview = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
if (!fs.existsSync(path.join(preview, 'index.html'))) throw new Error('Pass a generated character preview folder.');
if (process.env.BATCOMPUTER_REFRESH_PREVIEW_ASSETS === '1') {
  const source = path.resolve(__dirname, '../../Web/preview');
  for (const name of ['CharacterAnimationCreator.js', 'CharacterWorkshop.js', 'CharacterWorkshopShell.js', 'CharacterWorkshop.css'])
    fs.copyFileSync(path.join(source, name), path.join(preview, name));
}
const server = http.createServer((request, response) => {
  const file = path.resolve(preview, '.' + decodeURIComponent(request.url.split('?')[0]));
  if (!file.startsWith(preview + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
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
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
    await page.waitForFunction(() => !!window.characterAnimationCreator, null, { timeout:60000 });
    const drafts = await page.evaluate(() => {
      const body = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')];
      const bones = new Map(); body.traverse(node => { if (node.isBone && !bones.has(node.name)) bones.set(node.name, node); });
      const rigSignature = [...bones].map(([name, bone]) => `${name}:${bone.parent?.isBone ? bone.parent.name : ''}`).join('|');
      const key = (frame, degrees = [0,0,0]) => ({ frame, p:[0,0,0],
        q:new THREE.Quaternion().setFromEuler(new THREE.Euler(...degrees.map(value => value * Math.PI / 180), 'XYZ')).toArray() });
      const make = (name, description, durationFrames, tracks) => ({
        schema:'batcomputer.animation-draft.v1', name, description, fps:30, durationFrames, rigSignature,
        tracks:Object.entries(tracks).map(([bone, keys]) => ({ bone, keys:keys.map(([frame, degrees]) => key(frame, degrees)) }))
      });
      return [
        make('IdleBreathingTest', 'Gentle chest and shoulder breathing loop', 60, {
          Chest:[[0,[0,0,0]],[15,[3,0,0]],[30,[0,0,0]],[45,[-2,0,0]],[60,[0,0,0]]],
          Shoulder_L:[[0,[0,0,0]],[15,[0,0,2]],[30,[0,0,0]],[45,[0,0,-1]],[60,[0,0,0]]],
          Shoulder_R:[[0,[0,0,0]],[15,[0,0,-2]],[30,[0,0,0]],[45,[0,0,1]],[60,[0,0,0]]]
        }),
        make('IdleLookAroundTest', 'Head turns to each side and returns to center', 90, {
          Head:[[0,[0,0,0]],[20,[23,0,0]],[40,[0,0,0]],[65,[-20,0,0]],[90,[0,0,0]]],
          Neck:[[0,[0,0,0]],[20,[4,0,0]],[40,[0,0,0]],[65,[-4,0,0]],[90,[0,0,0]]]
        }),
        make('IdleWaveTest', 'Right arm lift and short wave before returning to rest', 90, {
          Shoulder_R:[[0,[0,0,0]],[20,[0,0,75]],[35,[0,0,75]],[50,[0,0,75]],[65,[0,0,75]],[90,[0,0,0]]],
          Elbow_R:[[0,[0,0,0]],[20,[0,0,-15]],[35,[0,0,-25]],[50,[0,0,-10]],[65,[0,0,-25]],[90,[0,0,0]]],
          WristRoll_R:[[0,[0,0,0]],[20,[0,0,0]],[35,[0,15,0]],[50,[0,-15,0]],[65,[0,15,0]],[90,[0,0,0]]]
        })
      ];
    });
    fs.mkdirSync(output, { recursive:true });
    const nativeRig = await page.evaluate(() => {
      const body = root.children[models.findIndex(entry => entry.part === 'CharacterMesh0')];
      const bones = []; body.traverse(node => { if (node.isBone) bones.push(node); });
      return bones.map(bone => ({ name:bone.name, parent:bone.parent?.isBone ? bone.parent.name : null,
        translation:[bone.position.x, bone.position.z, bone.position.y].map(n => n * 100),
        rotation:[-bone.quaternion.x, -bone.quaternion.z, -bone.quaternion.y, bone.quaternion.w],
        scale:[bone.scale.x, bone.scale.z, bone.scale.y] }));
    });
    fs.writeFileSync(path.join(output, 'NativeBodyRigReference.json'), JSON.stringify(nativeRig, null, 2) + '\n');
    await page.getByRole('button', { name:'Create', exact:true }).click();
    const resizeHandle = page.locator('.cw-creator-resize');
    const originalDockHeight = await page.locator('.cw-creator-dock').evaluate(el => el.getBoundingClientRect().height);
    const originalTrackHeight = await page.locator('.cw-creator-scroll').evaluate(el => el.getBoundingClientRect().height);
    const grip = await resizeHandle.boundingBox();
    await page.mouse.move(grip.x + grip.width / 2, grip.y + grip.height / 2);
    await page.mouse.down();
    await page.mouse.move(grip.x + grip.width / 2, grip.y + grip.height / 2 - 120, { steps:6 });
    await page.mouse.up();
    const tallerDockHeight = await page.locator('.cw-creator-dock').evaluate(el => el.getBoundingClientRect().height);
    const tallerTrackHeight = await page.locator('.cw-creator-scroll').evaluate(el => el.getBoundingClientRect().height);
    if (tallerDockHeight < originalDockHeight + 80 || tallerTrackHeight < originalTrackHeight + 80)
      throw new Error('Dragging the timeline top edge did not enlarge the dock and track area.');
    await resizeHandle.focus();
    await page.keyboard.press('ArrowDown');
    const adjustedDockHeight = await page.locator('.cw-creator-dock').evaluate(el => el.getBoundingClientRect().height);
    if (adjustedDockHeight > tallerDockHeight - 25) throw new Error('Keyboard timeline resize did not shrink the dock.');
    const savedDockHeight = await page.evaluate(() => Number(localStorage.getItem('batcomputer.animationTimelineHeight')));
    if (Math.abs(savedDockHeight - adjustedDockHeight) > 2) throw new Error('Timeline height was not remembered.');
    await page.getByRole('button', { name:'Motion', exact:true }).click();
    await page.getByRole('button', { name:'Create', exact:true }).click();
    if (Math.abs(await page.locator('.cw-creator-dock').evaluate(el => el.getBoundingClientRect().height) - savedDockHeight) > 2)
      throw new Error('Timeline height changed after leaving and reopening the Create tab.');
    console.log('Resizable timeline regression: drag, keyboard, track growth, and persistence pass');
    for (const draft of drafts) {
      for (const track of draft.tracks) if (!draft.rigSignature.includes(`${track.bone}:`)) throw new Error('Missing native bone: ' + track.bone);
      const json = JSON.stringify(draft, null, 2) + '\n';
      fs.writeFileSync(path.join(output, draft.name + '.json'), json);
      await page.locator('#cw-animation-creator .file').setInputFiles({
        name:draft.name + '.json', mimeType:'application/json', buffer:Buffer.from(json) });
      await page.waitForFunction(name => document.querySelector('#cw-animation-creator .status').textContent.startsWith('Loaded'), draft.name);
      if (await page.locator('#cw-animation-creator input[data-kind="scale"][data-axis="0"]').inputValue() !== '1')
        throw new Error('An older position/rotation-only draft did not default to rest scale.');
      const slider = page.locator('#cw-animation-creator .timeline');
      await slider.evaluate((element, frame) => { element.value = String(frame); element.oninput(); },
        draft.name === 'IdleBreathingTest' ? 15 : draft.name === 'IdleLookAroundTest' ? 20 : 35);
      await page.screenshot({ path:path.join(output, draft.name + '.png') });
      console.log(`${draft.name}: ${draft.tracks.length} native bone tracks, ${draft.durationFrames} frames`);
    }
    await page.locator('[aria-label="Animation bone"]').selectOption('Head');
    await page.evaluate(() => document.activeElement.blur());
    for (const [shortcut, mode, button] of [['w','translate','.move'], ['r','rotate','.rotate'], ['e','scale','.scale']]) {
      await page.keyboard.press(shortcut);
      if (await page.evaluate(() => window.characterAnimationCreator.gizmo.getMode()) !== mode ||
          !await page.locator('#cw-animation-creator ' + button).evaluate(el => el.classList.contains('active')))
        throw new Error(`${shortcut.toUpperCase()} did not activate the ${mode} bone transform.`);
    }
    await page.keyboard.press('r');
    await page.locator('.cw-creator-dock .dock-timeline').evaluate(el => { el.value = '44'; el.oninput(); });
    const gizmoCheck = await page.evaluate(() => {
      const gizmo = window.characterAnimationCreator.gizmo;
      if (gizmo.object?.name !== 'Head' || gizmo.getMode() !== 'rotate') throw new Error('3D rotate tool did not attach to the selected bone.');
      gizmo.dispatchEvent({ type:'dragging-changed', value:true });
      gizmo.object.quaternion.multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1,0,0), .11));
      gizmo.dispatchEvent({ type:'objectChange' });
      gizmo.dispatchEvent({ type:'dragging-changed', value:false });
      return [...document.querySelectorAll('.cw-creator-lane.selected .cw-key-marker')].some(node => node.title === 'Head · frame 44');
    });
    if (!gizmoCheck) throw new Error('3D rotation did not create a keyframe on the selected track.');
    await page.locator('.cw-creator-dock .dock-timeline').evaluate(el => { el.value = '46'; el.oninput(); });
    await page.keyboard.press('e');
    const scaleCheck = await page.evaluate(() => {
      const gizmo = window.characterAnimationCreator.gizmo;
      if (gizmo.getMode() !== 'scale' || gizmo.object?.name !== 'Head') throw new Error('Scale handle is not on the selected bone.');
      gizmo.dispatchEvent({ type:'dragging-changed', value:true });
      gizmo.object.scale.multiplyScalar(1.2);
      gizmo.dispatchEvent({ type:'objectChange' });
      gizmo.dispatchEvent({ type:'dragging-changed', value:false });
      return [...document.querySelectorAll('#cw-animation-creator input[data-kind="scale"]')].map(input => Number(input.value));
    });
    if (scaleCheck.some(value => Math.abs(value - 1.2) > .01)) throw new Error('3D scale did not key all bone scale axes.');
    await page.locator('.cw-creator-dock .dock-timeline').evaluate(el => { el.value = '45'; el.oninput(); });
    const interpolatedScale = Number(await page.locator('#cw-animation-creator input[data-kind="scale"][data-axis="0"]').inputValue());
    if (interpolatedScale <= 1 || interpolatedScale >= 1.2) throw new Error('Bone scale did not interpolate between keys.');
    await page.locator('#cw-animation-creator .undo').click();
    if (await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Head · frame 46"]').count())
      throw new Error('Undo did not remove the bone scale key.');
    await page.locator('#cw-animation-creator .redo').click();
    if (await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Head · frame 46"]').count() !== 1)
      throw new Error('Redo did not restore the bone scale key.');
    await page.locator('.cw-creator-dock .first').click();
    if (await page.locator('.cw-creator-dock .dock-time').innerText() !== '0 / 90 · 0.00s')
      throw new Error('Timeline did not return to frame zero.');
    await page.locator('.cw-creator-dock .next').click();
    if (!(await page.locator('.cw-creator-dock .dock-time').innerText()).startsWith('1 / 90'))
      throw new Error('Single-frame transport failed.');
    await page.screenshot({ path:path.join(output, 'AnimationCreatorTimeline.png') });
    console.log('Timeline and 3D rotation keyframe regression: pass');
    await page.locator('.cw-creator-dock .dock-timeline').evaluate(el => { el.value = '44'; el.oninput(); });
    await page.locator('#cw-animation-creator .copy-key').click();
    await page.locator('[aria-label="Animation bone"]').selectOption('Neck');
    await page.locator('.cw-creator-dock .dock-timeline').evaluate(el => { el.value = '55'; el.oninput(); });
    await page.locator('#cw-animation-creator .paste-key').click();
    if (await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Neck · frame 55"]').count() !== 1)
      throw new Error('Copy/paste did not make a key on the selected bone and frame.');
    await page.locator('#cw-animation-creator .undo').click();
    if (await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Neck · frame 55"]').count())
      throw new Error('Undo did not remove the pasted key.');
    await page.locator('#cw-animation-creator .redo').click();
    await page.locator('#cw-animation-creator .interpolation').selectOption('hold');
    if (await page.locator('#cw-animation-creator .interpolation').inputValue() !== 'hold')
      throw new Error('Key transition was not updated.');
    await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Neck · frame 55"]').evaluate(marker => {
      const rail = marker.parentElement, rect = rail.getBoundingClientRect();
      const x = rect.left + rect.width * 55 / 90;
      marker.dispatchEvent(new PointerEvent('pointerdown', { bubbles:true, button:0, clientX:x }));
      window.dispatchEvent(new PointerEvent('pointermove', { bubbles:true, clientX:rect.left + rect.width * 60 / 90 }));
      window.dispatchEvent(new PointerEvent('pointerup', { bubbles:true, clientX:rect.left + rect.width * 60 / 90 }));
    });
    if (await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Neck · frame 60"]').count() !== 1)
      throw new Error('Dragging a timeline diamond did not retime the key.');
    await page.locator('#cw-animation-creator .undo').click();
    if (await page.locator('.cw-creator-lane.selected .cw-key-marker[title="Neck · frame 55"]').count() !== 1)
      throw new Error('Undo did not restore the retimed key.');
    await page.locator('#cw-animation-creator .toggle-joints').click();
    const rigUi = await page.evaluate(() => ({
      namedBones:document.querySelectorAll('.cw-creator-bone-list button').length,
      jointMarkers:scene.children.filter(child => child.userData?.boneName && child.visible).length,
      selectedMarker:scene.children.find(child => child.userData?.boneName === 'Neck')?.position.toArray(),
      boneWorld:window.characterAnimationCreator.gizmo.object.getWorldPosition(new THREE.Vector3()).toArray()
    }));
    if (rigUi.namedBones < 30 || rigUi.jointMarkers < 30 ||
        rigUi.selectedMarker.some((value, axis) => Math.abs(value - rigUi.boneWorld[axis]) > .0001))
      throw new Error('The skeleton hierarchy or joint markers did not match the selected bone.');
    await page.locator('#cw-animation-creator .toggle-joints').click();
    const fitWidth = await page.locator('.cw-creator-ruler').evaluate(el => el.getBoundingClientRect().width);
    await page.locator('.cw-creator-dock .zoom').selectOption('4');
    const zoomWidth = await page.locator('.cw-creator-ruler').evaluate(el => el.getBoundingClientRect().width);
    if (zoomWidth <= fitWidth) throw new Error('Timeline zoom did not widen the keyframe canvas.');
    await page.locator('#cw-animation-creator .clip-name').fill('Wave polish');
    await page.locator('#cw-animation-creator .clip-name').dispatchEvent('change');
    await page.locator('#cw-animation-creator .clip-description').fill('A short test gesture');
    await page.locator('#cw-animation-creator .clip-description').dispatchEvent('change');
    const downloadReady = page.waitForEvent('download');
    await page.locator('#cw-animation-creator .save').click();
    const download = await downloadReady;
    const saved = JSON.parse(fs.readFileSync(await download.path(), 'utf8'));
    if (saved.name !== 'Wave polish' || saved.description !== 'A short test gesture' ||
        !saved.tracks.some(track => track.bone === 'Neck' && track.keys.some(key => key.frame === 55 && key.interpolation === 'hold')) ||
        !saved.tracks.some(track => track.bone === 'Head' && track.keys.some(key => key.frame === 46 && key.s?.every(value => Math.abs(value - 1.2) < .01))))
      throw new Error('Saved draft lost clip metadata, key timing, interpolation, or bone scale.');
    await page.locator('#cw-animation-creator .file').setInputFiles({
      name:'wave-polish.json', mimeType:'application/json', buffer:Buffer.from(JSON.stringify(saved)) });
    await page.waitForFunction(() => document.querySelector('#cw-animation-creator .status').textContent.startsWith('Loaded'));
    if (await page.locator('#cw-animation-creator .clip-name').inputValue() !== 'Wave polish')
      throw new Error('Saved draft metadata was not restored.');
    await page.locator('.cw-creator-dock .dock-timeline').evaluate(el => { el.value = '46'; el.oninput(); });
    await page.locator('[aria-label="Animation bone"]').selectOption('Head');
    if (Math.abs(Number(await page.locator('#cw-animation-creator input[data-kind="scale"][data-axis="0"]').inputValue()) - 1.2) > .01)
      throw new Error('Loading the saved draft did not restore the bone scale key.');
    await page.locator('#cw-animation-creator .new').click();
    if (await page.locator('#cw-animation-creator .clip-name').inputValue() !== 'New animation' ||
        await page.locator('.cw-creator-lane .cw-key-marker').count())
      throw new Error('New draft did not reset the clip and its tracks.');
    console.log('Copy/paste, undo/redo, transition, key drag, hierarchy, joints, zoom, and draft lifecycle regression: pass');
    if (process.env.BATCOMPUTER_AXIS_PROBE === '1') {
      for (const bone of ['Head', 'Shoulder_R', 'Elbow_R']) for (const axis of ['x', 'y', 'z']) for (const sign of [-1, 1]) {
        const degrees = [0, 0, 0]; degrees['xyz'.indexOf(axis)] = sign * 30;
        const probe = await page.evaluate(({ bone, degrees, rigSignature }) => ({
          schema:'batcomputer.animation-draft.v1', name:'AxisProbe', fps:30, durationFrames:30, rigSignature,
          tracks:[{ bone, keys:[
            { frame:0, p:[0,0,0], q:[0,0,0,1] },
            { frame:15, p:[0,0,0], q:new THREE.Quaternion().setFromEuler(new THREE.Euler(...degrees.map(v => v*Math.PI/180),'XYZ')).toArray() },
            { frame:30, p:[0,0,0], q:[0,0,0,1] }
          ] }]
        }), { bone, degrees, rigSignature:drafts[0].rigSignature });
        const json = JSON.stringify(probe);
        await page.locator('#cw-animation-creator .file').setInputFiles({ name:'AxisProbe.json', mimeType:'application/json', buffer:Buffer.from(json) });
        await page.waitForFunction(() => document.querySelector('#cw-animation-creator .status').textContent.startsWith('Loaded'));
        await page.locator('#cw-animation-creator .timeline').evaluate(el => { el.value = '15'; el.oninput(); });
        await page.screenshot({ path:path.join(output, `probe-${bone}-${axis}-${sign}.png`) });
      }
    }
    if (errors.length) throw new Error(errors.join('\n'));
  } finally { await browser.close(); server.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; server.close(); });
