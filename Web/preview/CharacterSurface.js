// EoM surface approximation: structural RAO uses UV0; decal MMR uses the selected atlas UV.
// R=structural roughness and G=AO are independently confirmed in the supplied Blender setup.
// Keep the game's packed masks as DATA (linear), not display colours. Weather/grease are not simulated.
window.BatcomputerCharacterSurface = function (THREE, material, rao) {
  if (!rao) return;
  const previous = material.onBeforeCompile, key = material.customProgramCacheKey();
  const enabled = { value: 1 };
  material.userData.eomSurface = { enabled };
  material.onBeforeCompile = function (shader, renderer) {
    previous.call(this, shader, renderer);
    shader.uniforms.bcSurfaceRao = { value: rao };
    shader.uniforms.bcSurfaceEnabled = enabled;
    shader.vertexShader = '#ifndef BC_HAS_UV0\n#define BC_HAS_UV0\nattribute vec2 aUv0;\n#endif\nvarying vec2 bcSurfaceUv;\n' + shader.vertexShader;
    shader.vertexShader = shader.vertexShader.replace('#include <uv_vertex>', '#include <uv_vertex>\nbcSurfaceUv=aUv0;');
    shader.fragmentShader = 'varying vec2 bcSurfaceUv; uniform sampler2D bcSurfaceRao; uniform float bcSurfaceEnabled;\n' + shader.fragmentShader;
    // The Blender reference blends decal B with structural RAO.R using B as the weight,
    // then applies its 0.146..1 roughness ramp. Our ORM already contains that ramp, so
    // undo it before blending and apply it only once. No MMR means structural roughness.
    shader.fragmentShader = shader.fragmentShader.replace('#include <roughnessmap_fragment>', `
#include <roughnessmap_fragment>
if (bcSurfaceEnabled > 0.5) {
  float bcDecalRoughness = 1.0;
  #ifdef USE_ROUGHNESSMAP
    bcDecalRoughness = clamp((texture2D(roughnessMap, vUv).g - (37.0/255.0)) / (218.0/255.0), 0.0, 1.0);
  #endif
  float bcStructuralRoughness = texture2D(bcSurfaceRao, bcSurfaceUv).r;
  roughnessFactor = (37.0/255.0) + (218.0/255.0) * mix(bcDecalRoughness, bcStructuralRoughness, bcDecalRoughness);
}`);
  };
  material.customProgramCacheKey = () => key + '|eom-structural-surface-v1';
  material.needsUpdate = true;
};
