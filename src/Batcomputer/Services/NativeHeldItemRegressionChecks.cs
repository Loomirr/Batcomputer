using System.Text.Json;
namespace Batcomputer;

internal static class NativeHeldItemRegressionChecks
{
    internal static void Run(List<string> failures, TextWriter output)
    {
        void Check(bool ok, string title) { output.WriteLine((ok?"PASS: ":"FAIL: ")+title); if(!ok)failures.Add(title); }
        var a="/Game/Abilities/GA_Claws";var b="/Game/Abilities/GA_Other";
        var catalog=new AbilityEditorCatalog {InheritedAbilitySets=[new(){PackagePath="/Game/Sets/AS_First",GameplayAbilities=[new(){PackagePath=a},new(){PackagePath=b}]},new(){PackagePath="/Game/Sets/AS_Off",GameplayAbilities=[new(){PackagePath="/Game/Abilities/GA_Disabled"}]}]};
        var profile=new AbilityLoadoutProfile {HeldItems=[],AbilitySets=[new(){PackagePath="/Game/Sets/AS_First",RemovedGameplayAbilities=[b],AddedGameplayAbilities=[new(){PackagePath="/Game/Abilities/GA_Added"}]},new(){PackagePath="/Game/Sets/AS_Off",Enabled=false}]};
        Check(NativeHeldItemService.ActiveAbilities(profile,catalog).SequenceEqual([a,"/Game/Abilities/GA_Added"]),"native held-item discovery respects enabled sets, removed grants and explicit additions");
        var edit=new NativeHeldItemEdit {AbilityPackage=a,ActorPackage="/Game/Actors/BP_Claws",ExportName="StaticMesh_GEN_VARIABLE",MeshProperty="StaticMesh",OriginalMeshPackage="/Game/Meshes/SM_Claws",AssetClass="StaticMesh",SourceFingerprint=new('A',64),Materials=[new(){Slot=0,Package="/Game/Materials/MI_Claws"}],Transform=new(){X=3}};
        var left=edit.Clone();left.ManagedItemIndex=1;
        Check(edit.Key!=left.Key&&NativeHeldItemService.Validate([edit,left]).Count==0,"native shared actor remains independently editable in both managed hand slots");
        Check(NativeHeldItemService.Validate([edit,edit.Clone()]).Count>0,"duplicate native visual bindings are rejected");
        var invalid=edit.Clone();invalid.AssetClass="SkeletalMesh";
        Check(NativeHeldItemService.Validate([invalid]).Count>0,"unsupported native skeletal replacements cannot be saved as static edits");
        invalid=edit.Clone();invalid.Transform!.ScaleX=float.NaN;
        Check(NativeHeldItemService.Validate([invalid]).Count>0,"invalid native component placement is rejected");
        invalid=edit.Clone();invalid.Materials.Add(new(){Slot=0,Package="/Game/Materials/MI_Other"});
        Check(NativeHeldItemService.Validate([invalid]).Count>0,"duplicate native material slot overrides are rejected");
        profile.NativeHeldItems=[edit,left];var fingerprint=AbilityLoadoutService.ConfigurationFingerprint(profile);
        var copy=AbilityExplorerForm.CloneProfile(profile);copy.NativeHeldItems[0].Materials[0].Package="/Game/Materials/MI_Other";copy.NativeHeldItems[0].Transform!.X=8;
        Check(edit.Materials[0].Package=="/Game/Materials/MI_Claws"&&edit.Transform!.X==3&&fingerprint!=AbilityLoadoutService.ConfigurationFingerprint(copy),"native visual edits deep-clone and invalidate generated assets");
        var roundtrip=JsonSerializer.Deserialize<AbilityLoadoutProfile>(JsonSerializer.Serialize(profile))!;
        Check(AbilityLoadoutService.ConfigurationFingerprint(roundtrip)==fingerprint,"native held-item edits survive saved project roundtrip");
        copy=AbilityExplorerForm.CloneProfile(profile);copy.NativeHeldItems[0].Hide=true;
        Check(AbilityLoadoutService.ConfigurationFingerprint(copy)!=fingerprint&&NativeHeldItemService.RedirectAbility(copy,"Fixture",a).StartsWith("/Game/Mods/Fixture/NativeHeldItems/")&&NativeHeldItemService.RedirectAbility(copy,"Fixture",b)==b,"native hide intent is cached and only the edited controller is redirected suit-locally");
        Check(AbilityLoadoutPresentation.HeldEntries(profile,"/Game/Mods/Fixture/Characters/BP_Test").Count==2,"native visual overrides appear in the ability loadout without extra props");
    }
}
