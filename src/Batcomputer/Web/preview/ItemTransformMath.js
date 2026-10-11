// Same centered OBJ, UE centimeters and yaw*pitch*roll basis as StaticMeshObjProbeService.
// Effect components use Unreal's rotator signs instead of the OBJ import's rotation signs.
window.BatcomputerItemTransformMath = function (THREE, effect = false) {
  const basis = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -Math.PI / 2);
  const clone = t => ({ scale: t.scale, offset: [...t.offset], rotation: [...t.rotation] });
  const valid = t => t && Number.isFinite(t.scale) && t.scale >= (effect ? .01 : .001) && t.scale <= (effect ? 10 : 1000) &&
    t.offset?.length === 3 && t.rotation?.length === 3 && t.offset.every(v => Number.isFinite(v) && Math.abs(v) <= (effect ? 1000 : 10000)) &&
    t.rotation.every(v => Number.isFinite(v) && Math.abs(v) <= 360);
  function rotation(a) {
    const r = Math.PI / 180, sign = effect ? -1 : 1;
    const q = new THREE.Quaternion().setFromEuler(new THREE.Euler(sign * a[2] * r, sign * a[0] * r, a[1] * r, 'ZYX'));
    return basis.clone().multiply(q).multiply(basis.clone().invert());
  }
  function toNode(node, t) {
    node.position.set(t.offset[0] / 100, t.offset[2] / 100, -t.offset[1] / 100);
    node.quaternion.copy(rotation(t.rotation)); node.scale.setScalar(t.scale);
  }
  function fromNode(node) {
    const q = basis.clone().invert().multiply(node.quaternion).multiply(basis);
    const e = new THREE.Euler().setFromQuaternion(q, 'ZYX'), deg = 180 / Math.PI, sign = effect ? -1 : 1;
    return { scale: node.scale.x, offset: [node.position.x * 100, -node.position.z * 100, node.position.y * 100],
      rotation: [sign * e.y * deg, e.z * deg, sign * e.x * deg] };
  }
  return { clone, valid, rotation, toNode, fromNode };
};
