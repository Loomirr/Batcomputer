/* Native idle-pose placement preview. Does not modify the cooked character or its skeleton. */
window.buildVehicleRider = async function (rider) {
  const loader = new THREE.GLTFLoader(), textures = new THREE.TextureLoader(), result = new THREE.Group();
  const reference = [], posed = [], deltas = new Map();
  const component = (a,i,j) => j===0?a.getX(i):j===1?a.getY(i):j===2?a.getZ(i):a.getW(i);
  const local = t => new THREE.Matrix4().compose(new THREE.Vector3(...t.slice(0,3)), new THREE.Quaternion(...t.slice(3,7)).normalize(), new THREE.Vector3(...t.slice(7,10)));
  rider.bones.forEach((b,i) => {
    reference[i] = local(b.reference); posed[i] = local(b.animated);
    if (b.parent >= 0) { reference[i].premultiply(reference[b.parent]); posed[i].premultiply(posed[b.parent]); }
    deltas.set(b.name, posed[i].clone().multiply(reference[i].clone().invert()));
  });
  const response = await fetch(rider.folder+'models.json'); if (!response.ok) throw new Error('Character preview is missing');
  const models = await response.json(), textureCache = new Map();
  async function texture(path, color) { if (!path) return null; const key=path+color; if (!textureCache.has(key)) {const t=await textures.loadAsync(rider.folder+path); t.flipY=false; if(color)t.encoding=THREE.sRGBEncoding; textureCache.set(key,t);} return textureCache.get(key); }
  for (const model of models.filter(m => !m.isface && m.part !== 'Cape')) {
    const g = await loader.loadAsync(rider.folder+model.file), source=g.scene;
    if (model.pos) source.position.fromArray(model.pos); if(model.rot)source.quaternion.fromArray(model.rot); if(model.scale)source.scale.fromArray(model.scale); source.position.add(new THREE.Vector3(...model.offset)); source.updateMatrixWorld(true);
    const meshes=[];source.traverse(n=>{if(n.isMesh)meshes.push(n);});
    const shading = await Promise.all(model.slots.map(async s=>new THREE.MeshStandardMaterial({color:s.col||0xffffff,map:await texture(s.tex,true),normalMap:await texture(s.nrm,false),alphaMap:await texture(s.alpha,false),alphaTest:s.cut?.5:0,roughness:s.rough??.5,metalness:s.metal??0,visible:!s.hide,side:THREE.DoubleSide})));
    for (const mesh of meshes) {
      const geometry=mesh.geometry.clone(), position=geometry.attributes.position, normal=geometry.attributes.normal;
      const indices=geometry.attributes.skinIndex, weights=geometry.attributes.skinWeight;
      const bodyRig=mesh.skeleton?.bones.some(b=>b.name==='Pelvis'), headPart=model.ishead||/head|hair|hat|helmet|cowl/i.test(model.part||'');
      const transform=bodyRig?null:headPart?deltas.get('Head_Attach_01')||deltas.get('Head'):deltas.get('Chest');
      const sourceNormal=new THREE.Matrix3().getNormalMatrix(mesh.matrixWorld), v=new THREE.Vector3(), n=new THREE.Vector3(), out=new THREE.Vector3(), outN=new THREE.Vector3(), temp=new THREE.Vector3();
      const boneMatrices=bodyRig?mesh.skeleton.bones.map(b=>deltas.get(b.name)||new THREE.Matrix4()):[];
      const normalMatrices=boneMatrices.map(m=>new THREE.Matrix3().getNormalMatrix(m));
      for(let i=0;i<position.count;i++) {
        v.fromBufferAttribute(position,i).applyMatrix4(mesh.matrixWorld); if(normal)n.fromBufferAttribute(normal,i).applyMatrix3(sourceNormal).normalize();
        if(bodyRig&&indices&&weights){out.set(0,0,0);outN.set(0,0,0);let total=0;for(let j=0;j<4;j++){const weight=component(weights,i,j);if(!weight)continue;const k=component(indices,i,j);temp.copy(v).applyMatrix4(boneMatrices[k]);out.addScaledVector(temp,weight);if(normal){temp.copy(n).applyMatrix3(normalMatrices[k]);outN.addScaledVector(temp,weight);}total+=weight;}if(total>0){v.copy(out).divideScalar(total);if(normal)n.copy(outN).normalize();}}
        else if(transform){v.applyMatrix4(transform);if(normal)n.applyMatrix3(new THREE.Matrix3().getNormalMatrix(transform)).normalize();}
        position.setXYZ(i,v.x,v.y,v.z);if(normal)normal.setXYZ(i,n.x,n.y,n.z);
      }
      // LEGO body/head artwork is on the second UV set; CUE glTF exposes it as uv2.
      if(model.uv===1&&geometry.attributes.uv2)geometry.setAttribute('uv',geometry.attributes.uv2.clone());
      geometry.deleteAttribute('skinIndex');geometry.deleteAttribute('skinWeight');geometry.computeBoundingBox();geometry.computeBoundingSphere();
      const original=Array.isArray(mesh.material)?mesh.material:[mesh.material];
      const mats=original.map(m=>{const slot=g.parser.json.materials.findIndex(x=>x.name===m.name);return shading[slot]||shading[0]||new THREE.MeshStandardMaterial({color:0x555963});});
      const posedMesh=new THREE.Mesh(geometry,Array.isArray(mesh.material)?mats:mats[0]);posedMesh.userData.rider=true; result.add(posedMesh);
    }
  }
  // Mount the authored seated-animation root at the seat. The standing collision-cylinder
  // offset is not a seat offset. Runtime mounting/IK still requires an in-game comparison.
  const actor=new THREE.Group();actor.add(result);
  actor.rotation.x=Math.PI/2;actor.scale.z=-1;actor.userData.rider=true;actor.userData.animation=rider.animation;
  return actor;
};
