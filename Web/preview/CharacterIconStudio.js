// LoTDK Icon Temp camera transforms are converted from Blender (X,Y,Z) to glTF (X,Z,-Y).
// This is a viewer-material approximation, not an Unreal or Eevee shader reproduction.
window.BatcomputerCharacterIconStudio = function ({ THREE, root, loaded, complete, withNeutralFace, canTestSuit = false, layoutKey = '', post }) {
  const presets = {
    menu: { label: 'Menu portrait · front', size: 512, lens: 89.67384338, position: [250.5667, 0, 145.4776], back: [1, 0, 0] },
    left: { label: 'Left-facing portrait', size: 512, lens: 89.67384338, position: [234.2692, 88.8910, 145.4776], back: [.9349574, .3547599, 0] },
    right: { label: 'Right-facing portrait', size: 512, lens: 89.67384338, position: [234.2692, -88.8910, 145.4776], back: [.9349574, -.3547599, 0] },
    suit: { label: 'Suit tile', size: 256, lens: 136.7235413, position: [323.639862, 0, 77.444336], back: [.999784, 0, -.0207822] }
  };
  // Numerical light settings only; no meshes, textures or artwork from the reference .blend.
  // Each rectangular emitter includes its world-space scaled axes and dimensions.
  const lights = [
    { p: [10.6613,61.5294,114.3043], power:25.8, color:[1,1,1] },
    { p: [-104.7084,-166.5668,49.1603], power:2111, color:[.170365,.478724,1], area:[585.45,144.63], back:[-.21035,-.97661,.04126], up:[.97343,-.20541,.10035] },
    { p: [68.4114,-45.5352,191.2195], power:1225.2, color:[.224711,.235116,.226706] },
    { p: [-41.6965,0,300.0317], power:2649.2, color:[.173829,.206956,.238222], area:[183.294,183.294], back:[0,0,1], up:[0,1,0] },
    { p: [164.699,-211.593,187.1911], power:10, color:[.030036,.030036,.030036], area:[929.2,929.2], back:[.73779,-.51027,.44216], up:[.558,.829,.0254] },
    { p: [-124.384,344.1017,58.3337], power:779.5, gain:100, color:[.992918,.314866,0], area:[585.45,144.63], back:[-.21035,.97661,.04126], up:[-.97343,-.20541,-.10035] },
    { p: [189.8587,-165.069,184.9688], power:222334.59375, color:[.085029,.085029,.085029], area:[56.156,56.156], back:[.254388,-.700019,.668160], up:[.894953,.433014,.112919] }
  ];
  const convert = a => new THREE.Vector3(a[0], a[2], -a[1]);
  let renderer = null, environment = null, dialog = null, currentCanvas = null, lightsInitialized = false, testPending = false, applyPending = false;
  const visibility = new Map(loaded.map(entry => [entry.scene, !entry.m.hidden && !entry.m.beside]));

  function render({ preset = 'menu', zoom = 1, lift = 0, exposure = 0, neutralFace = true } = {}) {
    if (!complete) throw new Error('Some character parts failed to load. Reload the viewer before rendering icons.');
    if (!presets[preset]) throw new Error('Unknown icon layout.');
    if (![zoom,lift,exposure].every(Number.isFinite) || zoom < .5 || zoom > 1.5 || Math.abs(lift) > .35 || Math.abs(exposure) > 2)
      throw new Error('Icon framing values are outside the supported range.');
    // The preview expression is a diagnostic control. Icons always capture the middle frame of
    // the game's neutral clip, then put the viewer back exactly where the user left it.
    const restoreFace = neutralFace ? withNeutralFace?.() : null;
    let snapshot;
    try {
      snapshot = window.BatcomputerCharacterExport.snapshot(THREE, root, visibility);
      if (!renderer) {
        renderer = new THREE.WebGLRenderer({ antialias:true, alpha:true, preserveDrawingBuffer:true });
        // PMREM render-target textures belong to their WebGL context. Build this one locally;
        // borrowing the main viewer's texture silently produces black metallic surfaces.
        const sky = document.createElement('canvas'); sky.width = 8; sky.height = 64;
        const context = sky.getContext('2d'), gradient = context.createLinearGradient(0,0,0,64);
        gradient.addColorStop(0,'#d6dde8'); gradient.addColorStop(.55,'#7a828e'); gradient.addColorStop(1,'#24272c');
        context.fillStyle = gradient; context.fillRect(0,0,8,64);
        const texture = new THREE.CanvasTexture(sky); texture.mapping = THREE.EquirectangularReflectionMapping;
        const pmrem = new THREE.PMREMGenerator(renderer); environment = pmrem.fromEquirectangular(texture);
        pmrem.dispose(); texture.dispose();
      }
      const scene = new THREE.Scene(); scene.add(snapshot.root);
      scene.environment = environment.texture;
      scene.add(new THREE.AmbientLight(new THREE.Color(.155,.155,.155), 1));
      const sun = new THREE.DirectionalLight(0xffffff, 1); sun.position.set(0,3,0); scene.add(sun);
      if (!lightsInitialized) { THREE.RectAreaLightUniformsLib.init(); lightsInitialized = true; }
      for (const item of lights) {
        const color = new THREE.Color(...item.color);
        // Radiometric Blender powers and this older WebGL renderer's photometric lights
        // do not share exposure units. Calibrated against the same Electric GLB in Eevee.
        // The warm emitter has Normalize disabled in the reference and needs a separate gain.
        const power = item.power * 30 * (item.gain || 1);
        const light = item.area
          ? new THREE.RectAreaLight(color, power / (Math.PI * item.area[0] * item.area[1]), item.area[0] * .01, item.area[1] * .01)
          : new THREE.PointLight(color, power * .0001 / (4 * Math.PI), 0, 2);
        light.position.copy(convert(item.p).multiplyScalar(.01));
        if (item.area) {
          light.up.copy(convert(item.up).normalize());
          light.lookAt(light.position.clone().sub(convert(item.back)));
        }
        scene.add(light);
      }
      const p = presets[preset];
      const camera = new THREE.PerspectiveCamera(THREE.MathUtils.radToDeg(2 * Math.atan(18 / p.lens)), 1, .001, 1000);
      camera.position.copy(convert(p.position).multiplyScalar(.01));
      camera.position.y += lift;
      camera.lookAt(camera.position.clone().sub(convert(p.back)));
      camera.zoom = zoom; camera.updateProjectionMatrix();
      // Downsample a larger render into the game-sized PNG. The 256px suit tile needs more
      // coverage samples than the portraits to keep thin printing and diagonal edges intact.
      const sampleScale = p.size <= 256 ? 4 : 3;
      renderer.setPixelRatio(1); renderer.setSize(p.size * sampleScale, p.size * sampleScale, false);
      renderer.setClearColor(0x000000, 0); renderer.outputEncoding = THREE.sRGBEncoding;
      renderer.physicallyCorrectLights = true;
      renderer.toneMapping = THREE.ACESFilmicToneMapping;
      renderer.toneMappingExposure = Math.pow(2, -.56644344 + exposure);
      scene.updateMatrixWorld(true);
      snapshot.root.traverse(node => { if (node.isSkinnedMesh) node.skeleton.update(); });
      renderer.render(scene, camera);
      const canvas = document.createElement('canvas'); canvas.width = canvas.height = p.size;
      const ctx = canvas.getContext('2d'); ctx.imageSmoothingQuality = 'high';
      ctx.drawImage(renderer.domElement, 0, 0, p.size, p.size);
      return canvas;
    } finally {
      snapshot?.root.traverse(node => { if (node.isSkinnedMesh) node.skeleton.dispose(); });
      snapshot?.dispose();
      renderer?.renderLists.dispose();
      restoreFace?.();
    }
  }
  async function download(canvas, preset) {
    if (!presets[preset] || !canvas) throw new Error('Render an icon first.');
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    if (!blob) throw new Error('PNG encoding failed.');
    const url = URL.createObjectURL(blob), link = document.createElement('a');
    link.href = url; link.download = `Suit-icon-${preset}-${canvas.width}.png`;
    document.body.appendChild(link); link.click(); link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }
  function previewStatus() {
    if (!currentCanvas || !dialog) return '';
    const actual = dialog.querySelector('.icon-preview')?.classList.contains('actual-pixels');
    return `${currentCanvas.width} × ${currentCanvas.height} saved PNG · ${actual ? 'actual pixels' : 'enlarged preview'}`;
  }
  function testResult(result) {
    testPending = false;
    if (!dialog) return;
    const button = dialog.querySelector('.icon-test');
    if (button) button.disabled = applyPending || !currentCanvas || dialog.querySelector('select').value !== 'suit';
    const applyButton = dialog.querySelector('.icon-apply');
    if (applyButton) applyButton.disabled = applyPending || !currentCanvas || dialog.querySelector('select').value !== 'suit';
    dialog.querySelector('.icon-status').textContent = result?.message || 'Suit icon test did not return a result.';
  }
  function applyResult(result) {
    applyPending = false;
    if (!dialog) return;
    const button = dialog.querySelector('.icon-apply');
    if (button) button.disabled = testPending || !currentCanvas || dialog.querySelector('select').value !== 'suit';
    const testButton = dialog.querySelector('.icon-test');
    if (testButton) testButton.disabled = testPending || !currentCanvas || dialog.querySelector('select').value !== 'suit';
    dialog.querySelector('.icon-status').textContent = result?.message || 'Suit icon assignment did not return a result.';
  }
  function release() { currentCanvas = null; environment?.dispose(); environment = null; renderer?.dispose(); renderer?.forceContextLoss(); renderer = null; }
  function open() {
    if (dialog) { dialog.showModal(); refresh(); return; }
    const style = document.createElement('style');
    style.textContent = `#suit-icon-studio{box-sizing:border-box;width:min(850px,95vw);max-height:94vh;overflow:auto;border:1px solid #4b5260;border-radius:12px;background:#20252e;color:#e8ecf2;padding:22px;font:14px 'Segoe UI',sans-serif}#suit-icon-studio::backdrop{background:#000b}#suit-icon-studio header{display:flex;justify-content:space-between;align-items:center}#suit-icon-studio h2{margin:0;font-size:22px}#suit-icon-studio p{color:#abb7c7;line-height:1.5}#suit-icon-studio .icon-grid{display:grid;grid-template-columns:minmax(180px,1fr) 235px;gap:22px}#suit-icon-studio .icon-preview{aspect-ratio:1;display:grid;place-items:center;background:repeating-conic-gradient(#303641 0% 25%,#262c35 0% 50%) 0/24px 24px;border-radius:8px;overflow:hidden}#suit-icon-studio canvas{width:auto;height:auto;max-width:100%;max-height:100%;object-fit:contain}#suit-icon-studio label{display:block;margin:0 0 18px}#suit-icon-studio select,#suit-icon-studio input{display:block;width:100%;margin-top:8px}#suit-icon-studio button,#suit-icon-studio select{font:inherit;background:#303845;color:#eee;border:1px solid #515e70;border-radius:6px;padding:9px 12px}#suit-icon-studio button{cursor:pointer}#suit-icon-studio button:disabled{opacity:.45;cursor:default}#suit-icon-studio .icon-save{background:#ffda43;color:#171b22;font-weight:600;width:100%;margin:14px 0}#suit-icon-studio small{display:block;color:#aeb8c8;line-height:1.5}@media(max-width:600px){#suit-icon-studio .icon-grid{grid-template-columns:1fr}#suit-icon-studio .icon-preview{max-height:42vh;justify-self:center;width:min(100%,42vh)}}`;
    style.textContent += '#suit-icon-studio .icon-stage{min-width:0}#suit-icon-studio .icon-preview canvas{width:100%;height:100%}#suit-icon-studio .icon-preview.actual-pixels canvas{width:auto;height:auto;max-width:100%;max-height:100%}#suit-icon-studio .icon-size-toggle{margin-top:8px}';
    document.head.appendChild(style);
    dialog = document.createElement('dialog'); dialog.id = 'suit-icon-studio';
    dialog.innerHTML = `<header><h2>Suit icon studio</h2><button type="button" aria-label="Close icon studio">Close</button></header><p>LoTDK template framing · transparent PNGs</p><div class="icon-grid"><div class="icon-preview"></div><div><label>Layout<select aria-label="Icon layout"></select></label><label>Zoom<input aria-label="Icon zoom" type="range" min="0.5" max="1.5" step="0.01" value="1"></label><label>Vertical framing<input aria-label="Icon vertical framing" type="range" min="-0.35" max="0.35" step="0.005" value="0"></label><label>Exposure<input aria-label="Icon exposure" type="range" min="-2" max="2" step="0.1" value="0"></label><button type="button" class="icon-reset">Reset framing</button><button type="button" class="icon-save">Save PNG…</button><small class="icon-status" role="status"></small></div></div><p><small>Uses the current character’s preview materials and a sampled neutral face pose. Separate gliders and native-hidden helpers are excluded. Full face animation and game shaders are not reproduced. Saving a PNG does not replace your suit’s icons.</small></p>`;
    const preview = dialog.querySelector('.icon-preview'), stage = document.createElement('div');
    stage.className = 'icon-stage'; preview.replaceWith(stage); stage.appendChild(preview);
    const sizeToggle = document.createElement('button');
    sizeToggle.type = 'button'; sizeToggle.className = 'icon-size-toggle'; sizeToggle.textContent = 'Actual pixels';
    sizeToggle.onclick = () => {
      const actual = preview.classList.toggle('actual-pixels');
      sizeToggle.textContent = actual ? 'Fit preview' : 'Actual pixels';
      dialog.querySelector('.icon-status').textContent = previewStatus();
    };
    stage.appendChild(sizeToggle);
    if (canTestSuit && layoutKey && typeof post === 'function') {
      const testButton = document.createElement('button');
      testButton.type = 'button'; testButton.className = 'icon-test';
      testButton.textContent = 'Test suit icon cook';
      testButton.title = 'Cook a separate 256px BC7 test texture. Does not change this suit, its icons or the game.';
      function submitSuitIcon(type) {
        if (testPending || applyPending || !currentCanvas || select.value !== 'suit') return;
        const png = currentCanvas.toDataURL('image/png');
        if (png.length > 1_500_000) {
          dialog.querySelector('.icon-status').textContent = 'The generated PNG is too large for an in-app icon cook. Save it and import manually.';
          return;
        }
        if (type === 'apply-suit-icon') applyPending = true; else testPending = true;
        testButton.disabled = true; applyButton.disabled = true;
        dialog.querySelector('.icon-status').textContent = type === 'apply-suit-icon'
          ? 'Waiting for confirmation in Batcomputer…' : 'Cooking a separate test icon… Your saved suit and game are untouched.';
        try { post({ type, layout: layoutKey, png }); }
        catch (error) {
          const result = { message: 'Could not send the icon: ' + error.message };
          if (type === 'apply-suit-icon') applyResult(result); else testResult(result);
        }
      }
      testButton.onclick = () => submitSuitIcon('test-suit-icon');
      const applyButton = document.createElement('button');
      applyButton.type = 'button'; applyButton.className = 'icon-apply';
      applyButton.textContent = 'Use as suit icon';
      applyButton.title = 'Confirm, cook and assign this image to the saved suit project. Does not install a mod.';
      applyButton.onclick = () => submitSuitIcon('apply-suit-icon');
      dialog.querySelector('.icon-save').before(testButton);
      dialog.querySelector('.icon-save').before(applyButton);
      const note = document.createElement('small');
      note.textContent = 'Test leaves the project untouched. Use as suit icon asks for confirmation and changes only the saved project; rebuild the mod to use it in-game.';
      applyButton.after(note);
    }
    document.body.appendChild(dialog);
    const select = dialog.querySelector('select');
    for (const [id,p] of Object.entries(presets)) { const option = document.createElement('option'); option.value = id; option.textContent = `${p.label} · ${p.size}px`; select.appendChild(option); }
    select.onchange = () => { reset(); refresh(); };
    const inputs = [...dialog.querySelectorAll('input')];
    let pending = 0;
    inputs.forEach(input => { input.oninput = () => { cancelAnimationFrame(pending); pending = requestAnimationFrame(refresh); }; });
    const close = dialog.querySelector('header button'); close.onclick = () => dialog.close();
    dialog.addEventListener('close', () => { cancelAnimationFrame(pending); release(); });
    function reset() { inputs.forEach((input,i) => { input.value = i === 0 ? '1' : '0'; }); }
    dialog.querySelector('.icon-reset').onclick = () => { reset(); refresh(); };
    dialog.querySelector('.icon-save').onclick = async () => {
      const button = dialog.querySelector('.icon-save'); button.disabled = true;
      try { await download(currentCanvas, select.value); dialog.querySelector('.icon-status').textContent = 'Choose where to save your PNG. Your suit project is unchanged.'; }
      catch (error) { dialog.querySelector('.icon-status').textContent = error.message; }
      finally { button.disabled = !currentCanvas; }
    };
    dialog.showModal(); refresh();
  }
  function refresh() {
    if (!dialog?.open) return;
    const status = dialog.querySelector('.icon-status'), save = dialog.querySelector('.icon-save');
    try {
      const values = [...dialog.querySelectorAll('input')].map(input => Number(input.value));
      currentCanvas = render({preset:dialog.querySelector('select').value,zoom:values[0],lift:values[1],exposure:values[2]});
      dialog.querySelector('.icon-preview').replaceChildren(currentCanvas); save.disabled = false;
      const testButton = dialog.querySelector('.icon-test');
      if (testButton) testButton.disabled = testPending || applyPending || dialog.querySelector('select').value !== 'suit';
      const applyButton = dialog.querySelector('.icon-apply');
      if (applyButton) applyButton.disabled = testPending || applyPending || dialog.querySelector('select').value !== 'suit';
      status.textContent = previewStatus();
    } catch (error) { currentCanvas = null; save.disabled = true; dialog.querySelector('.icon-preview').replaceChildren(); status.textContent = error.message; }
  }
  return { open, render, download, testResult, applyResult, dispose:release, presets };
};
