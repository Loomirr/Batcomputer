using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class FaceAnimationRegressionChecks
{
    internal static IEnumerable<(bool Passed,string Description)> Run()
    {
        var asset=new UAsset { Imports=[], Exports=[] };asset.ClearNameIndexList();
        ObjectPropertyData Ref(string name,string package,string kind)=>new(new FName(asset,name)) {
            Value=SwordCombatService.Obj(asset,package,UnrealPathUtil.AssetName(package)+(kind=="AnimBlueprintGeneratedClass"?"_C":""),"/Script/Engine",kind) };
        var mesh=Ref("SkeletalMesh",FaceAnimationService.FaceMesh,"SkeletalMesh");
        var material=Ref("OverrideMaterials","/Game/Mods/Example/MI_FACE_Example","MaterialInstanceConstant");
        var anim=Ref("AnimClass","/Game/Characters/Attachments/LEGOface/ABP_LEGOface_Catwoman","AnimBlueprintGeneratedClass");
        var body=Ref("AnimClass","/Game/Characters/LEGOfig/ABP_Core_LEGOFig","AnimBlueprintGeneratedClass");var bodyIndex=body.Value;
        var face=new NormalExport(asset,[]) { ObjectName=new FName(asset,"Face_GEN_VARIABLE"),Data=[mesh,anim,material] };asset.Exports.Add(face);
        var materialIndex=material.Value;var meshIndex=mesh.Value;
        var alias = new ObjectPropertyData(new FName(asset,"SkinnedAsset")) { Value = FPackageIndex.FromRawIndex(0) };
        face.Data.Add(alias);
        FaceAnimationService.Apply(asset,FaceAnimationService.BruceWayne);
        var count=asset.Imports.Count;FaceAnimationService.Apply(asset,FaceAnimationService.BruceWayne);
        yield return (SwordCombatService.Package(asset,anim.Value)==FaceAnimationService.BruceWayne && material.Value==materialIndex && mesh.Value==meshIndex && body.Value==bodyIndex && asset.Imports.Count==count && face.CreateBeforeSerializationDependencies.Count==1,
            "face-only controller swaps preserve material/mesh/body references and repeated application is idempotent");
        mesh.Value=FPackageIndex.FromRawIndex(0);
        var savedAnim=anim.Value;var savedImports=asset.Imports.Count;var savedDependencies=face.CreateBeforeSerializationDependencies.Count;
        yield return (!FaceAnimationService.Apply(asset,FaceAnimationService.BruceWayne) && anim.Value==savedAnim &&
            asset.Imports.Count==savedImports && face.CreateBeforeSerializationDependencies.Count==savedDependencies && mesh.Value.IsNull() && alias.Value.IsNull(),
            "removed face skips inherited controller without restoring mesh or changing asset references");
        asset.Exports.Remove(face);
        yield return (!FaceAnimationService.Apply(asset,FaceAnimationService.BruceWayne), "absent face component skips inherited controller");
        asset.Exports.Add(face);
        alias.Value=meshIndex;
        yield return (FaceAnimationService.Apply(asset,FaceAnimationService.BruceWayne), "SkinnedAsset-only face accepts native rig with a null SkeletalMesh alias");
        alias.Value=FPackageIndex.FromRawIndex(0);
        mesh.Value=SwordCombatService.Obj(asset,"/Game/Characters/OtherRig/SK_Other","SK_Other","/Script/Engine","SkeletalMesh");
        bool Rejected(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        yield return (Rejected(()=>FaceAnimationService.Apply(asset,FaceAnimationService.BruceWayne))&&Rejected(()=>FaceAnimationService.Apply(asset,"/Game/Arbitrary/ABP")),
            "facial controller selection rejects incompatible meshes and unverified controller donors");
        var project=new NativeSuitProject { FaceAnimationBlueprintPackage=FaceAnimationService.BruceWayne };
        var saved=System.Text.Json.JsonSerializer.Deserialize<NativeSuitProject>(System.Text.Json.JsonSerializer.Serialize(project))!;
        var identity=UAssetPatchService.BuildStageIdentityForTest(project);project.FaceAnimationBlueprintPackage="";
        yield return (saved.FaceAnimationBlueprintPackage==FaceAnimationService.BruceWayne && identity!=UAssetPatchService.BuildStageIdentityForTest(project) &&
            CharacterRebaseService.Sections().Single(s=>s.Id=="animations").Fields.Contains(nameof(NativeSuitProject.FaceAnimationBlueprintPackage)),
            "facial-controller recipes save, invalidate stale staging and follow explicit animation rebases");
    }
}
