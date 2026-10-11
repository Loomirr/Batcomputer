/* Shared-pivot transforms without reparenting bones, sockets or preview meshes. */
(function (root) {
  'use strict';
  function begin(THREE, nodes, pivot) {
    pivot.updateMatrixWorld(true);
    return { inverse: pivot.matrixWorld.clone().invert(), entries: nodes.map(node => {
      node.updateWorldMatrix(true, false);
      return { node, world: node.matrixWorld.clone(), parentInverse: node.parent.matrixWorld.clone().invert() };
    }) };
  }
  function evaluate(THREE, start, pivot) {
    pivot.updateMatrixWorld(true);
    const delta = pivot.matrixWorld.clone().multiply(start.inverse);
    return start.entries.map(entry => {
      const matrix = entry.parentInverse.clone().multiply(delta).multiply(entry.world);
      const node = new THREE.Object3D();
      matrix.decompose(node.position, node.quaternion, node.scale);
      node.updateMatrix();
      // A local TRS recipe cannot represent shear. Reject the entire group instead
      // of saving a subtly different transform for a rotated/nonuniform parent.
      if (matrix.elements.some((v, i) => !Number.isFinite(v) || Math.abs(v - node.matrix.elements[i]) > 0.00001))
        throw new Error('These parent transforms cannot represent this group rotation/scale. Move them together or edit them separately.');
      return { target: entry.node, node };
    });
  }
  const api = { begin, evaluate };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.VehicleGroupTransform = api;
})(typeof window === 'undefined' ? globalThis : window);
