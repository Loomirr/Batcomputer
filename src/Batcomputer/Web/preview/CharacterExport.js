// Reference assembly export; Unreal shaders and animation blueprints are not portable to GLB.
window.BatcomputerCharacterExport = {
  snapshot(THREE, root, visibility) {
    const copy = THREE.SkeletonUtils.clone(root), cloned = new Map(), disposable = [];
    function pair(source, target) {
      cloned.set(source, target);
      source.children.forEach((child, index) => pair(child, target.children[index]));
    }
    pair(root, copy);
    // Ignore preview-only isolation without touching the live scene.
    visibility.forEach((value, object) => { if (cloned.has(object)) cloned.get(object).visible = value; });
    copy.position.set(0, 0, 0); // Remove frameAll's camera-centering translation only.
    try {
      copy.traverse(node => {
        if (!node.isMesh) return;
        if (node.isSkinnedMesh && node.skeleton.bones.some(bone => !bone))
          throw new Error('This part references a rig outside the assembly. Export its native rig separately.');
        // Ghosting is a temporary placement aid, never a material edit in the exported reference.
        const restore = m => { if (!m?.userData.previewGhostOriginal) return m;
          const copy = m.clone(); Object.assign(copy, m.userData.previewGhostOriginal);
          delete copy.userData.previewGhostOriginal; disposable.push(copy); return copy; };
        node.material = Array.isArray(node.material) ? node.material.map(restore) : restore(node.material);
        const materials = Array.isArray(node.material) ? node.material : [node.material];
        // Unreal vertex colours often encode masks, not albedo. The viewer disables their colour
        // multiplication; glTF has no such material flag, so retaining COLOR_0 darkens Blender imports.
        if (node.geometry.attributes.color && materials.every(m => !m || !m.vertexColors)) {
          const geometry = node.geometry.clone(); disposable.push(geometry);
          geometry.deleteAttribute('color'); node.geometry = geometry;
        }
        if (materials.every(material => !material || material.visible === false)) { node.visible = false; return; }
        if (Array.isArray(node.material) && materials.some(material => !material || material.visible === false)) {
          const geometry = node.geometry.clone(); disposable.push(geometry);
          geometry.groups = geometry.groups.filter(group => materials[group.materialIndex] && materials[group.materialIndex].visible !== false);
          node.geometry = geometry;
        }
        for (const material of materials) {
          if (!material || material.visible === false) continue;
          for (const value of Object.values(material)) {
            if (!value?.isTexture) continue;
            const image = value.image;
            if (!image || image.complete === false || (image.naturalWidth !== undefined && image.naturalWidth === 0))
              throw new Error('A preview texture is not ready. Wait for loading to finish and try again.');
          }
        }
      });
      copy.name = 'Batcomputer assembled character';
      copy.userData = { description: 'Preview assembly with available rigs and approximate PBR materials. No game animation blueprints, cloth simulation or gameplay logic.' };
      copy.updateMatrixWorld(true);
      return { root: copy, dispose: () => disposable.forEach(geometry => geometry.dispose()) };
    } catch (error) { disposable.forEach(geometry => geometry.dispose()); throw error; }
  },
  async download(THREE, root, visibility) {
    const snapshot = this.snapshot(THREE, root, visibility);
    try {
      const binary = await new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('Export timed out. Reload the preview and try again.')), 60000);
        try {
          new THREE.GLTFExporter().parse(snapshot.root, result => { clearTimeout(timer); resolve(result); },
            { binary: true, onlyVisible: true, embedImages: true, maxTextureSize: 2048 });
        } catch (error) { clearTimeout(timer); reject(error); }
      });
      const url = URL.createObjectURL(new Blob([binary], { type: 'model/gltf-binary' }));
      const link = document.createElement('a'); link.href = url; link.download = 'Character-assembled.glb';
      document.body.appendChild(link); link.click(); link.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60000);
    } finally { snapshot.dispose(); }
  }
};
