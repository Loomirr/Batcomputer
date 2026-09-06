using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;

namespace Batcomputer;

internal static class SkinnedMeshPreviewService
{
    internal static string Create(string directory, SkinnedMeshImport recipe)
    {
        SkinnedMeshStageService.ReadManifest(directory, recipe);
        var folder = ModelPreviewService.BuildPreview(AppSettings.Current.GamePaksRoot!, AppSettings.Current.EffectiveUsmapPath()!, recipe.DonorMeshPackage);
        using var provider = SkinnedMeshCookService.OpenProvider(Path.Combine(SkinnedMeshCookService.SafePath(directory, recipe.CacheRelativePath), "AuditContainers"));
        var mesh = provider.LoadPackageObject<USkeletalMesh>(recipe.MeshPackage);
        var exporter = new MeshExporter(mesh, new ExporterOptions { MeshFormat = EMeshFormat.Gltf2, LodFormat = ELodFormat.FirstLod, ExportMaterials = false, ExportMorphTargets = false });
        if (!exporter.TryWriteToDir(new DirectoryInfo(Path.Combine(folder, "custom")), out _, out var file)) throw new InvalidDataException("Custom skinned preview could not be exported.");
        File.Copy(file, Path.Combine(folder, "custom.glb"));
        File.WriteAllText(Path.Combine(folder, "index.html"), Viewer);
        return folder;
    }
    private const string Viewer = """
<!doctype html><html><head><meta charset="utf-8"><style>
body{margin:0;background:#20242b;color:#eee;font:14px sans-serif}canvas{display:block}#bar{position:absolute;top:10px;left:12px;right:12px;background:#20242bdd;padding:10px;border-radius:8px}label{display:inline-block;margin:4px}select,button{background:#343b46;color:white;padding:5px;border:1px solid #677381}#note{font-size:12px;margin-top:8px;max-width:600px}
</style></head><body><div id="bar"><label><input id="original" type="checkbox">Original</label><label><input id="custom" type="checkbox" checked>Custom</label><label><input id="bones" type="checkbox" checked>Skeleton</label><button id="frame">Frame</button><br>
<label>Pose-check bone <select id="bone"><option value="">Rest pose</option></select></label><label>Axis <select id="axis"><option>X</option><option>Y</option><option>Z</option></select></label><label>Angle <input id="angle" type="range" min="-90" max="90" value="0"></label><button id="reset">Reset pose</button>
<div id="note">Pose check only—not game animation playback. Colors identify material slots; final game shaders are not simulated. Rig alignment must be corrected in the source FBX.</div><div id="error"></div></div>
<script src="three.min.js"></script><script src="GLTFLoader.js"></script><script src="OrbitControls.js"></script><script src="models.js"></script><script>
const scene=new THREE.Scene(),camera=new THREE.PerspectiveCamera(38,innerWidth/innerHeight,.001,10000),renderer=new THREE.WebGLRenderer({antialias:true});renderer.setSize(innerWidth,innerHeight);renderer.setPixelRatio(Math.min(devicePixelRatio,2));renderer.outputEncoding=THREE.sRGBEncoding;document.body.appendChild(renderer.domElement);
scene.add(new THREE.HemisphereLight(0xe5efff,0x303944,1.2));const key=new THREE.DirectionalLight(0xffffff,1.4);key.position.set(2,4,3);scene.add(key);const controls=new THREE.OrbitControls(camera,renderer.domElement),loader=new THREE.GLTFLoader();let original=null,custom=null,helpers=[],bones=[],rest=[];
const byId=id=>document.getElementById(id);function frame(){const box=new THREE.Box3().setFromObject(custom||original);if(box.isEmpty())return;const center=box.getCenter(new THREE.Vector3()),size=Math.max(box.getSize(new THREE.Vector3()).length(),.1);controls.target.copy(center);camera.position.copy(center).add(new THREE.Vector3(size*.6,size*.4,size));controls.update();}
async function load(){const refs=window.PREVIEW_MODELS||[];if(refs.length){original=(await loader.loadAsync(refs[0].file)).scene;original.visible=false;original.traverse(n=>{if(n.isMesh)n.material=new THREE.MeshStandardMaterial({color:0xa6b0bd,roughness:.65});});scene.add(original);}
custom=(await loader.loadAsync('custom.glb')).scene;const palette=[0xf4cb39,0x41c9db,0xc887ee,0xf58955,0x83cf80,0xf071a5];let slot=0;custom.traverse(n=>{if(n.isMesh){const make=()=>new THREE.MeshStandardMaterial({color:palette[slot++%palette.length],roughness:.6,skinning:!!n.isSkinnedMesh});n.material=Array.isArray(n.material)?n.material.map(make):make();}if(n.isBone){bones.push(n);rest.push(n.quaternion.clone());}});scene.add(custom);const helper=new THREE.SkeletonHelper(custom);scene.add(helper);helpers.push(helper);bones.forEach((b,i)=>{const option=document.createElement('option');option.value=i;option.textContent=b.name;byId('bone').appendChild(option);});frame();}
function pose(){bones.forEach((b,i)=>b.quaternion.copy(rest[i]));const value=byId('bone').value;if(value!==''){const q=new THREE.Quaternion(),axis=byId('axis').value,v=new THREE.Vector3(axis==='X'?1:0,axis==='Y'?1:0,axis==='Z'?1:0);q.setFromAxisAngle(v,Number(byId('angle').value)*Math.PI/180);bones[Number(value)].quaternion.multiply(q);}if(custom)custom.updateMatrixWorld(true);}
byId('original').onchange=e=>{if(original)original.visible=e.target.checked;};byId('custom').onchange=e=>{if(custom)custom.visible=e.target.checked;};byId('bones').onchange=e=>helpers.forEach(h=>h.visible=e.target.checked);byId('bone').onchange=pose;byId('axis').onchange=pose;byId('angle').oninput=pose;byId('reset').onclick=()=>{byId('bone').value='';byId('angle').value=0;pose();};byId('frame').onclick=frame;
load().catch(e=>byId('error').textContent=String(e));addEventListener('resize',()=>{camera.aspect=innerWidth/innerHeight;camera.updateProjectionMatrix();renderer.setSize(innerWidth,innerHeight);});function tick(){requestAnimationFrame(tick);controls.update();renderer.render(scene,camera);}tick();
</script></body></html>
""";
}
