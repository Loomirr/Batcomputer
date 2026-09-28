/* Boost preview for the vehicle workshop: an approximate flame at every boost / exhaust outlet, tinted like the chosen boost.
   The flames are children of the outlet parts, so they follow moves live. Nothing here is saved, and it is not the game's
   Niagara effect: shape, size and timing are stand-ins that show where and in which direction the boost fires. */
window.createVehicleBoostPreview = function (ctx) {
  'use strict';
  const { data, loaded, viewbar } = ctx, boost = data.boost || null;
  const outlets = Array.from(loaded.values()).filter(s => boost?.outlets?.includes(s.part.socket));
  const button = document.createElement('button'); button.id = 'boostPreview'; button.textContent = 'Boost preview';
  button.disabled = !boost || !outlets.length;
  button.title = !boost ? 'This driving base has no boost' : !outlets.length ? 'No boost outlet found on this rig'
    : boost.label + (boost.twin ? ' · twin exhausts' : '') + '. Approximate flames at each outlet; not the game effect.';
  viewbar.appendChild(button);

  const colour = new THREE.Color(boost?.colour || '#ff8a2a'), hot = new THREE.Color(0xfff4c8), cold = new THREE.Color(0x000000);
  const UP = new THREE.Vector3(0, 1, 0), COUNT = 90, LENGTH = .9;
  // Soft round sprite for the particles.
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = 64;
  const g2 = canvas.getContext('2d'), gradient = g2.createRadialGradient(32, 32, 0, 32, 32, 32);
  gradient.addColorStop(0, 'rgba(255,255,255,1)'); gradient.addColorStop(.35, 'rgba(255,255,255,.55)'); gradient.addColorStop(1, 'rgba(255,255,255,0)');
  g2.fillStyle = gradient; g2.fillRect(0, 0, 64, 64);
  const sprite = new THREE.CanvasTexture(canvas);
  const additive = extra => Object.assign({ transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide }, extra);
  // Open cone along +Y: widest at the outlet, fading to nothing at the tip (vertex colour, added over the scene).
  const cone = (radius, length) => {
    const geometry = new THREE.ConeGeometry(radius, length, 24, 6, true); geometry.translate(0, length / 2, 0);
    const y = geometry.attributes.position, fade = new Float32Array(y.count * 3);
    for (let i = 0; i < y.count; i++) { const k = Math.pow(1 - y.getY(i) / length, 1.6); fade[i * 3] = fade[i * 3 + 1] = fade[i * 3 + 2] = k; }
    geometry.setAttribute('color', new THREE.BufferAttribute(fade, 3)); return geometry;
  };

  const flames = outlets.map(state => {
    const root = new THREE.Group(); root.visible = false; root.userData.boostPreview = true;
    root.quaternion.setFromUnitVectors(UP, new THREE.Vector3().fromArray(state.part.markerDirection || [0, 0, 1]).normalize());
    const outer = new THREE.Mesh(cone(.13, LENGTH), new THREE.MeshBasicMaterial(additive({ color: colour, opacity: .45, vertexColors: true })));
    const inner = new THREE.Mesh(cone(.065, LENGTH * .5), new THREE.MeshBasicMaterial(additive({ color: hot, opacity: .8, vertexColors: true })));
    const positions = new Float32Array(COUNT * 3), colours = new Float32Array(COUNT * 3);
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3)); geometry.setAttribute('color', new THREE.BufferAttribute(colours, 3));
    const points = new THREE.Points(geometry, new THREE.PointsMaterial(additive({ size: .14, map: sprite, vertexColors: true, sizeAttenuation: true })));
    points.frustumCulled = false;
    const particles = Array.from({ length: COUNT }, () => spawn({}, Math.random()));
    const glow = new THREE.PointLight(colour, 0, 3.5, 2); glow.position.set(0, .25, 0);
    root.add(outer, inner, points, glow); state.node.add(root);
    return { root, outer, inner, points, positions, colours, particles, glow, seed: Math.random() * 10 };
  });
  function spawn(p, age) {
    const angle = Math.random() * Math.PI * 2, r = Math.random() * .05;
    p.x = Math.cos(angle) * r; p.z = Math.sin(angle) * r; p.y = 0;
    p.vx = Math.cos(angle) * (.1 + Math.random() * .25); p.vz = Math.sin(angle) * (.1 + Math.random() * .25); p.vy = 1.3 + Math.random() * .9;
    p.life = .25 + Math.random() * .22; p.age = age * p.life; return p;
  }

  let active = false, last = 0, started = 0;
  function show(open) {
    active = open; button.classList.toggle('active', open); flames.forEach(f => f.root.visible = open);
    started = last = performance.now() / 1000;
    const status = document.getElementById('status');
    if (status && open) status.textContent = 'Boost preview · ' + boost.label + (boost.twin ? ' · twin exhausts' : '') + ' · approximate flames, not the game effect';
  }
  button.onclick = () => show(!active);
  const mix = new THREE.Color();
  function update() {
    if (!active) return;
    const now = performance.now() / 1000, dt = Math.min(now - last, .05), t = now - started; last = now;
    // A short activation burst, then a flickering loop.
    const burst = 1 + .45 * Math.max(0, 1 - t / .35);
    for (const f of flames) {
      const flicker = .88 + .12 * Math.sin(now * 37 + f.seed) * Math.sin(now * 23 + f.seed * 2);
      f.outer.scale.set(burst, flicker * burst, burst); f.inner.scale.set(1, (.9 + .1 * Math.sin(now * 51 + f.seed)) * burst, 1);
      f.glow.intensity = 1.6 * flicker * burst;
      f.particles.forEach((p, i) => {
        p.age += dt; if (p.age >= p.life) spawn(p, 0);
        p.x += p.vx * dt; p.y += p.vy * dt * burst; p.z += p.vz * dt;
        const k = p.age / p.life, j = i * 3;
        f.positions[j] = p.x; f.positions[j + 1] = p.y; f.positions[j + 2] = p.z;
        if (k < .3) mix.copy(hot).lerp(colour, k / .3); else mix.copy(colour).lerp(cold, (k - .3) / .7);
        f.colours[j] = mix.r; f.colours[j + 1] = mix.g; f.colours[j + 2] = mix.b;
      });
      f.points.geometry.attributes.position.needsUpdate = true; f.points.geometry.attributes.color.needsUpdate = true;
    }
  }
  return { update, show, get active() { return active; }, get outlets() { return outlets.map(s => s.part.socket); } };
};
