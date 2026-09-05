using System.Text.Json;

namespace Batcomputer;

internal static class WeaponModelService
{
    internal const int MaximumSourceLength = 8 * 1024 * 1024;
    internal static void Validate(WeaponModelRecipe r)
    {
        if (string.IsNullOrWhiteSpace(r.ObjText) || r.ObjText.Length > MaximumSourceLength)
            throw new InvalidDataException("Import an OBJ smaller than 8 MB.");
        if (!float.IsFinite(r.Scale) || r.Scale < 0.001f || r.Scale > 1000)
            throw new InvalidDataException("Model scale must be between 0.001 and 1000.");
        if (new[] { r.X, r.Y, r.Z, r.Pitch, r.Yaw, r.Roll }.Any(v => !float.IsFinite(v)) ||
            new[] { r.Pitch, r.Yaw, r.Roll }.Any(v => Math.Abs(v) > 360))
            throw new InvalidDataException("Invalid weapon transform.");
        if (r.Materials.Count == 0 || r.Materials.Any(m => string.IsNullOrWhiteSpace(m.MaterialPath) ||
            !ExtractedPackagePathService.IsContentPackagePath(m.MaterialPath) || m.MaterialPath.Contains("..")))
            throw new InvalidDataException("Assign a cooked material package to every model slot.");
    }

    internal static string WriteSource(WeaponModelRecipe r, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "weapon.obj");
        File.WriteAllText(path, r.ObjText);
        return path;
    }

    internal static void Preview(WeaponModelRecipe r, string folder, bool original, bool custom, int revision)
    {
        Validate(r);
        var obj = WriteSource(r, folder);
        var file = $"custom-{revision}.glb";
        StaticMeshObjProbeService.WritePreviewGlb(obj, Path.Combine(folder, file), r.Scale, r.X, r.Y, r.Z,
            r.Pitch, r.Yaw, r.Roll, r.Materials);
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "weapon.json"), JsonSerializer.Serialize(new { file, original, custom, revision }));
    }

    internal static void Bake(WeaponModelRecipe r, string extracted, string mappings, string content, string package)
    {
        Validate(r);
        var scratch = Path.Combine(Path.GetTempPath(), "Batcomputer-weapon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = WriteSource(r, scratch);
            var result = new StaticMeshObjProbeService().CreateObjHeadProbe(new()
            {
                ExtractedContentRoot = extracted, UsmapPath = mappings, OutputContentRoot = content,
                OutputPackagePath = package, ObjPath = source, Scale = r.Scale, OffsetX = r.X, OffsetY = r.Y, OffsetZ = r.Z,
                RotationPitch = r.Pitch, RotationYaw = r.Yaw, RotationRoll = r.Roll, MaterialSlots = r.Materials,
            });
            if (result.Status != "created") throw new InvalidDataException(result.Error ?? "Weapon bake failed.");
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    internal static string CreateViewer(string referencePackage)
    {
        var settings = AppSettings.Current;
        var objectPath = referencePackage.Contains('.') ? referencePackage : referencePackage + "." + UnrealPathUtil.AssetName(referencePackage);
        var folder = ModelPreviewService.BuildPreview(settings.GamePaksRoot!, settings.EffectiveUsmapPath()!, objectPath);
        if (!Directory.EnumerateFiles(Path.Combine(folder, "export"), "*.glb", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("The reference weapon could not be exported. Choose an available native static mesh.");
        File.WriteAllText(Path.Combine(folder, "index.html"), Viewer);
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "weapon.json"), "{\"original\":true,\"custom\":true,\"revision\":0}");
        AtomicFileUtil.WriteAllText(Path.Combine(folder, "effects.json"), "[]");
        return folder;
    }

    private const string Viewer = """
<!doctype html><html><head><meta charset="utf-8"><style>
body{margin:0;background:#1d2026;color:#eee;font:14px sans-serif}canvas{display:block}#bar{position:absolute;top:8px;left:8px}button{padding:8px;background:#343943;color:white;border:1px solid #777}#error{color:#ffb454}
</style></head><body><div id="bar"><button id="frame">Frame models</button> Origin axes: X red · Y green · Z blue (viewer basis). Orbit: drag; zoom: wheel.<div id="error"></div></div>
<script src="three.min.js"></script><script src="GLTFLoader.js"></script><script src="OrbitControls.js"></script><script src="models.js"></script><script>
const scene=new THREE.Scene(),camera=new THREE.PerspectiveCamera(40,innerWidth/innerHeight,.001,1000),renderer=new THREE.WebGLRenderer({antialias:true});
renderer.setSize(innerWidth,innerHeight);renderer.setClearColor(0x232830);renderer.setPixelRatio(Math.min(devicePixelRatio,2));renderer.outputEncoding=THREE.sRGBEncoding;document.body.appendChild(renderer.domElement);
scene.add(new THREE.HemisphereLight(0xd9eaff,0x343238,1));for(const [x,y,z,p] of [[2,3,4,1.2],[-3,2,-2,.8]]){const l=new THREE.DirectionalLight(0xffffff,p);l.position.set(x,y,z);scene.add(l);}
scene.add(new THREE.AxesHelper(.15));const reference=new THREE.Group();scene.add(reference);let custom=null,revision=-1,busy=false;
const loader=new THREE.GLTFLoader(),controls=new THREE.OrbitControls(camera,renderer.domElement);camera.position.set(.6,.4,.6);
function frame(){const box=new THREE.Box3();if(reference.visible)box.setFromObject(reference);if(custom&&custom.visible)box.union(new THREE.Box3().setFromObject(custom));if(effectRoot.children.length)box.union(new THREE.Box3().setFromObject(effectRoot));if(box.isEmpty())return;const center=box.getCenter(new THREE.Vector3()),size=Math.max(box.getSize(new THREE.Vector3()).length(),.05);controls.target.copy(center);camera.position.copy(center).add(new THREE.Vector3(size,size*.65,size));controls.update();}
document.getElementById('frame').onclick=frame;
Promise.all((window.PREVIEW_MODELS||[]).map(m=>new Promise((resolve,reject)=>loader.load(m.file,g=>{g.scene.traverse(n=>{if(n.isMesh)n.material=new THREE.MeshStandardMaterial({color:0x9aafc1,roughness:.55,metalness:.1});});reference.add(g.scene);resolve();},undefined,reject)))).then(frame).catch(e=>document.getElementById('error').textContent='Reference failed: '+e);
async function poll(){if(busy)return;busy=true;try{const r=await fetch('weapon.json?t='+Date.now(),{cache:'no-store'}),s=await r.json();reference.visible=s.original;if(custom)custom.visible=s.custom;if(s.file&&revision!==s.revision){const g=await loader.loadAsync(s.file);if(custom){scene.remove(custom);custom.traverse(n=>{if(n.geometry)n.geometry.dispose();if(n.material){for(const m of (Array.isArray(n.material)?n.material:[n.material]))m.dispose();}});}custom=g.scene;let slot=0;custom.traverse(n=>{if(n.isMesh)n.material=new THREE.MeshStandardMaterial({color:[0xf7c832,0x39bfc7,0xce74eb,0xeb8456][slot++%4],roughness:.65,metalness:0});});custom.visible=s.custom;scene.add(custom);revision=s.revision;document.getElementById('error').textContent='Alignment shaders: neutral original, colored custom slots. Final game materials are not rendered here.';}}catch(e){document.getElementById('error').textContent=String(e);}finally{busy=false;}}
setInterval(poll,400);addEventListener('resize',()=>{camera.aspect=innerWidth/innerHeight;camera.updateProjectionMatrix();renderer.setSize(innerWidth,innerHeight);});
const effectRoot=new THREE.Group();scene.add(effectRoot);let effectState='',effectBusy=false,firstEffects=true;
function clearEffects(){while(effectRoot.children.length){const n=effectRoot.children[0];effectRoot.remove(n);n.traverse(o=>{if(o.geometry)o.geometry.dispose();if(o.material)o.material.dispose();});}}
async function pollEffects(){if(effectBusy)return;effectBusy=true;try{const r=await fetch('effects.json?t='+Date.now(),{cache:'no-store'}),raw=await r.text();if(raw===effectState)return;const data=JSON.parse(raw);clearEffects();effectState=raw;
for(const e of data){const g=new THREE.Group();g.position.set(e.X*.01,e.Z*.01,-e.Y*.01);g.scale.setScalar(e.Scale);
// Unreal rotator -> glTF basis (X,Z,-Y); positive pitch raises the native forward axis.
const rad=Math.PI/180,qYaw=new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0,1,0),e.Yaw*rad),qPitch=new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0,0,1),e.Pitch*rad),qRoll=new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1,0,0),-e.Roll*rad);g.quaternion.copy(qYaw.multiply(qPitch).multiply(qRoll));
const axes=new THREE.AxesHelper(.12);axes.material.depthTest=false;axes.renderOrder=20;g.add(axes);const marker=new THREE.Mesh(new THREE.SphereGeometry(.02,10,8),new THREE.MeshBasicMaterial({color:0xffffff,wireframe:true,depthTest:false}));marker.renderOrder=20;g.add(marker);
for(let i=0;i<14;i++){const m=new THREE.Mesh(new THREE.SphereGeometry(e.Shape==='cloud'?.028:.008,6,4),new THREE.MeshBasicMaterial({color:e.Color,transparent:true,opacity:.35,depthWrite:false,depthTest:false}));m.renderOrder=19;m.userData={particle:true,i,shape:e.Shape};g.add(m);}effectRoot.add(g);}
if(firstEffects&&data.length){firstEffects=false;frame();}
}catch(e){document.getElementById('error').textContent='Effect preview: '+e;}finally{effectBusy=false;}}
setInterval(pollEffects,350);
function tick(){requestAnimationFrame(tick);const t=performance.now()*.001;effectRoot.traverse(o=>{if(!o.userData.particle)return;const {i,shape}=o.userData,p=(t*.5+i/14)%1,a=i*2.399;
o.position.set(shape==='trail'?-.22*p:Math.cos(a)*.035*p,shape==='trail'?Math.sin(p*3)*.015:p*.16,Math.sin(a)*.025*p);o.material.opacity=(1-p)*(shape==='cloud'?.25:.65);});controls.update();renderer.render(scene,camera);}tick();
</script></body></html>
""";
}
