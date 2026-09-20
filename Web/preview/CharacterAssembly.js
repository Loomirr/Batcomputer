// Read-only native attachment offsets and exploded display of associated gliders.
window.BatcomputerVisibleBounds = function (THREE, objects) {
  const result = new THREE.Box3();
  objects.forEach(object => {
    object.updateMatrixWorld(true);
    object.traverse(node => {
      if (!node.isMesh) return;
      for (let parent = node; parent; parent = parent.parent) if (!parent.visible) return;
      if ((Array.isArray(node.material) ? node.material : [node.material]).every(m => m?.visible === false)) return;
      node.geometry.computeBoundingBox();
      result.union(node.geometry.boundingBox.clone().applyMatrix4(node.matrixWorld));
    });
  });
  return result;
};
window.BatcomputerCharacterAssembly = function (THREE, loaded) {
  const body = loaded.find(x => x.m.part === 'CharacterMesh0');
  const bones = new Map(), before = new Map();
  if (body) {
    body.scene.updateMatrixWorld(true);
    body.scene.traverse(b => { if (b.isBone) { bones.set(b.name, b); before.set(b.name, b.getWorldPosition(new THREE.Vector3())); } });
  }
  function offsets(entry) {
    const records = new Map((entry.m.boneOffsets || []).filter(o => o.bone && o.Offset).map(o => [o.bone, o.Offset]));
    let applied = false;
    entry.scene.traverse(b => {
      const o = b.isBone && records.get(b.name); if (!o) return;
      const t = o.Translation || {}, r = o.Rotation || {}, s = o.Scale3D || {};
      const values = [t.X ?? 0, t.Y ?? 0, t.Z ?? 0, r.X ?? 0, r.Y ?? 0, r.Z ?? 0, r.W ?? 1, s.X ?? 1, s.Y ?? 1, s.Z ?? 1];
      if (!values.every(Number.isFinite)) return;
      // CUE glTF basis: UE (X,Y,Z) -> (X,Z,-Y), centimeters -> meters.
      const shift = new THREE.Vector3(values[0], values[2], -values[1]).multiplyScalar(.01);
      b.position.add(shift.multiply(b.scale).applyQuaternion(b.quaternion));
      const basis = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -Math.PI / 2);
      const rotation = new THREE.Quaternion(values[3], values[4], values[5], values[6]);
      b.quaternion.multiply(basis.clone().multiply(rotation).multiply(basis.clone().invert()).normalize());
      b.scale.multiply(new THREE.Vector3(values[7], values[9], values[8])); applied = true;
    });
    entry.scene.updateMatrixWorld(true); return applied;
  }
  if (body) offsets(body);
  const deltas = new Map([...bones].map(([name, bone]) => [name, bone.getWorldPosition(new THREE.Vector3()).sub(before.get(name))]));
  loaded.forEach(entry => {
    if (entry !== body && !offsets(entry) && deltas.has(entry.m.anchor))
      entry.scene.position.add(deltas.get(entry.m.anchor));
    entry.scene.visible = !entry.m.hidden;
  });
  const character = window.BatcomputerVisibleBounds(THREE, loaded.filter(x => !x.m.beside).map(x => x.scene));
  if (character.isEmpty()) return;
  const gap = Math.max(character.getSize(new THREE.Vector3()).length() * .12, .1);
  let edge = character.max.z + gap; // Across the screen in the viewer's +X front view.
  loaded.filter(x => x.m.beside).forEach(entry => {
    let box = window.BatcomputerVisibleBounds(THREE, [entry.scene]); if (box.isEmpty()) return;
    const size = box.getSize(new THREE.Vector3());
    // Present the broad surface to the front camera, not the thin edge of its flying/rest pose.
    const axis = size.y < size.x && size.y < size.z ? new THREE.Vector3(0, 0, 1)
      : size.z < size.x && size.z < size.y ? new THREE.Vector3(0, 1, 0) : null;
    if (axis) {
      const rotation = new THREE.Quaternion().setFromAxisAngle(axis, Math.PI / 2), center = box.getCenter(new THREE.Vector3());
      entry.scene.position.sub(center).applyQuaternion(rotation).add(center);
      entry.scene.quaternion.premultiply(rotation); entry.scene.updateMatrixWorld(true);
      box = window.BatcomputerVisibleBounds(THREE, [entry.scene]);
    }
    const shift = new THREE.Vector3(character.getCenter(new THREE.Vector3()).x - box.getCenter(new THREE.Vector3()).x,
      character.min.y - box.min.y, edge - box.min.z);
    entry.scene.position.add(shift); entry.scene.updateMatrixWorld(true);
    entry.scene.userData.previewDisplayOffset = shift.toArray();
    edge += box.max.z - box.min.z + gap;
  });
};
