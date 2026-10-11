// Structural LEGO normals live on UV0, independently of the printed/decal atlas UV.
// Compose in view space so meshes carrying authored tangents don't discard the detail layer.
window.BatcomputerCharacterNormals = function (THREE, material, texture, micro, microTile = 1, microStrength = 0) {
  if (!texture) return;
  const previous = material.onBeforeCompile, cache = material.customProgramCacheKey.bind(material);
  const key = cache();
  const enabled = { value: 1 }, microEnabled = { value: 1 };
  material.userData.structuralNormal = { enabled, microEnabled, hasMicro: !!micro };
  material.extensions = { ...material.extensions, derivatives: true };
  material.onBeforeCompile = function (shader, renderer) {
    previous.call(this, shader, renderer);
    shader.uniforms.bcStructuralNormal = { value: texture };
    shader.uniforms.bcStructuralEnabled = enabled;
    if (micro) {
      shader.uniforms.bcMicroNormal = { value: micro };
      shader.uniforms.bcMicroTile = { value: microTile };
      shader.uniforms.bcMicroStrength = { value: microStrength };
      shader.uniforms.bcMicroEnabled = microEnabled;
    }
    shader.vertexShader = '#ifndef BC_HAS_UV0\n#define BC_HAS_UV0\nattribute vec2 aUv0;\n#endif\nvarying vec2 bcStructuralUv;\n' + shader.vertexShader;
    shader.vertexShader = shader.vertexShader.replace('#include <uv_vertex>', '#include <uv_vertex>\nbcStructuralUv = aUv0;');
    shader.fragmentShader = `varying vec2 bcStructuralUv;
uniform sampler2D bcStructuralNormal;
uniform float bcStructuralEnabled;
${micro ? 'uniform sampler2D bcMicroNormal; uniform float bcMicroTile; uniform float bcMicroStrength; uniform float bcMicroEnabled;' : ''}
vec3 bcPerturb(vec3 eye, vec3 n, vec3 sampleNormal, float facing, vec2 uv) {
  vec3 q0 = dFdx(eye), q1 = dFdy(eye);
  vec2 st0 = dFdx(uv), st1 = dFdy(uv);
  vec3 t = cross(q1,n)*st0.x + cross(n,q0)*st1.x;
  vec3 b = cross(q1,n)*st0.y + cross(n,q0)*st1.y;
  float det = max(dot(t,t),dot(b,b));
  float scale = det == 0.0 ? 0.0 : facing*inversesqrt(det);
  return normalize(t*sampleNormal.x*scale + b*sampleNormal.y*scale + n*sampleNormal.z);
}
` + shader.fragmentShader;
    shader.fragmentShader = shader.fragmentShader.replace('#include <normal_fragment_maps>',
      `vec3 bcUnperturbed = normal;
#include <normal_fragment_maps>
// Unreal DNRM/LEGO normal textures are DirectX tangent-space normals (green points down).
// Three.js/glTF uses the opposite Y convention. The regular material normalMap path applies
// normalScale.y = -1; mirror that here for this independent UV0 structural-detail layer.
vec3 bcSample = texture2D(bcStructuralNormal, bcStructuralUv).xyz*2.0-1.0;
bcSample.y = -bcSample.y;
vec3 bcDetail = bcPerturb(-vViewPosition, bcUnperturbed, bcSample, faceDirection, bcStructuralUv);
normal = normalize(normal + (bcDetail-bcUnperturbed)*bcStructuralEnabled);
${micro ? `// Third normal layer stays tiled at runtime so GPU mip selection preserves close-up grain.
vec2 bcMicroUv = bcStructuralUv * bcMicroTile;
vec2 bcMicroXY = (texture2D(bcMicroNormal, bcMicroUv).rg * 2.0 - 1.0) * bcMicroStrength;
vec3 bcMicroSample = normalize(vec3(bcMicroXY.x, -bcMicroXY.y, 1.0));
vec3 bcMicroDetail = bcPerturb(-vViewPosition, normal, bcMicroSample, faceDirection, bcMicroUv);
normal = normalize(mix(normal, bcMicroDetail, bcMicroEnabled));` : ''}`);
  };
  material.customProgramCacheKey = () => key + (micro ? '|structural-micro-uv0-v2' : '|structural-uv0-v1');
  material.needsUpdate = true;
};
