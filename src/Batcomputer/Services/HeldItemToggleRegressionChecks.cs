using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using Newtonsoft.Json.Linq;

namespace Batcomputer;

internal static class HeldItemToggleRegressionChecks
{
    internal static IReadOnlyList<(bool Passed,string Description)> Run()
    {
        var results=new List<(bool,string)>();
        const string prefix="/Game/Mods/Example/Toggle/";
        var p=new HeldItemToggleProfile { ItemId="claws",SourceOwner="Example",SourceRequestTag="Status.Batcomputer.HeldItem.Example.claws",SourceBusyTag="Status.Batcomputer.Toggle.Busy",SourceContextTag="Animation.Context.Batcomputer.Sheathed",
            ReplacedAbility="/Game/Characters/Abilities/Focus",RemovedFailureAbility="/Game/Characters/Abilities/NoFocus",ManagerPackage=prefix+"Manager",TogglePackage=prefix+"Toggle",IdlePackage=prefix+"Idle",WalkPackage=prefix+"Walk",RunPackage=prefix+"Run",SprintPackage=prefix+"Sprint" };
        var data=new byte[]{1,2,3};var hash=Convert.ToHexString(SHA256.HashData(data));
        foreach(var package in new[]{p.ManagerPackage,p.TogglePackage,p.IdlePackage,p.WalkPackage,p.RunPackage,p.SprintPackage}) p.Assets.Add(new(){Package=package,UassetBase64=Convert.ToBase64String(data),UexpBase64=Convert.ToBase64String(data),UassetSha256=hash,UexpSha256=hash});
        var profile=new AbilityLoadoutProfile { HeldItems=[new(){Id="claws"}],HeldItemToggle=p };
        p.SheathedAnimationReplacements[prefix+"ArmedIdle"]=p.IdlePackage;
        HeldItemToggleService.Validate(profile);
        var grants = new AbilityAssetMutationService.AbilitySetInspection { Success = true, GameplayAbilities = [
            new() { PackagePath = p.ReplacedAbility, AbilityLevel = 7, InputTag = "InputTag.UseFocus" },
            new() { PackagePath = p.RemovedFailureAbility }, new() { PackagePath = "/Game/Characters/Abilities/Other" }] };
        var legacy = HeldItemToggleService.InputEdits(grants, p, prefix + "OutputToggle");
        p.Version = 2; p.PreserveNativeTakedown = true; HeldItemToggleService.Validate(profile);
        var priority = HeldItemToggleService.InputEdits(grants, p, prefix + "OutputToggle");
        var addition = priority.Single(edit => edit.Kind == AbilityAssetMutationService.GameplayAbilityEditKind.Add);
        results.Add((addition.TargetPackagePath == prefix + "OutputToggle" && addition.AbilityLevelOverride == 7 && addition.InputTagOverride == "InputTag.UseFocus" &&
            !priority.Any(edit => edit.TargetPackagePath == p.ReplacedAbility) && priority.Count(edit => edit.Kind == AbilityAssetMutationService.GameplayAbilityEditKind.Remove) == 1,
            "native-priority toggle keeps the original takedown grant and shares its exact Focus input metadata"));
        results.Add((legacy.Any(edit => edit.Kind == AbilityAssetMutationService.GameplayAbilityEditKind.Replace && edit.TargetPackagePath == p.ReplacedAbility),
            "legacy item-toggle bundles retain their saved replacement behavior"));
        p.Version = 3; p.SourceInputEventTag = HeldItemToggleService.InputEventTag(p.SourceOwner,p); HeldItemToggleService.Validate(profile);
        p.Version = 4; HeldItemToggleService.Validate(profile);
        var observed = HeldItemToggleService.InputEdits(grants,p,prefix+"OutputToggle");
        results.Add((observed.Single(e=>e.Kind==AbilityAssetMutationService.GameplayAbilityEditKind.Add) is { InputTagOverride:"", AbilityLevelOverride:7 } &&
            !observed.Any(e=>e.TargetPackagePath==p.ReplacedAbility),
            "non-consuming mapped-input toggle is event-only and leaves the native Focus grant untouched"));
        var clone=AbilityExplorerForm.CloneProfile(profile);
        clone.HeldItemToggle!.Enabled=false;
        results.Add((p.Enabled && !clone.HeldItemToggle.Enabled && !ReferenceEquals(p.Assets,clone.HeldItemToggle.Assets),"ability editor deep-copies the complete private item-toggle bundle"));
        results.Add((AbilityLoadoutService.ConfigurationFingerprint(profile)!=AbilityLoadoutService.ConfigurationFingerprint(clone),"toggle enable/disable invalidates the saved ability build fingerprint"));
        bool Reject(Action<AbilityLoadoutProfile> edit) { var copy=AbilityExplorerForm.CloneProfile(profile);edit(copy);try { HeldItemToggleService.Validate(copy);return false; } catch { return true; } }
        results.Add((Reject(c=>c.HeldItems=[]) && Reject(c=>c.HeldItemToggle!.Assets.RemoveAt(0)),"toggle rejects a missing controlled prop or incomplete cooked closure"));
        results.Add((Reject(c=>c.HeldItemToggle!.Assets[0].UassetSha256="broken") && Reject(c=>c.HeldItemToggle!.Assets[0].Package="/Game/Characters/Native"),"toggle blocks corrupt payloads and native package replacements"));
        results.Add((Reject(c=>c.HeldItemToggle!.SheathedAnimationReplacements[prefix+"ArmedIdle"]="/Game/Mods/Other/Missing")&&
            Reject(c=>c.HeldItemToggle!.SheathedAnimationReplacements["../unsafe"]=p.IdlePackage),"sheathed animation pairs reject unsafe selectors or private replacements missing from the portable bundle"));
        results.Add((Reject(c=>c.HeldItemToggle!.SourceRequestTag="Status.OtherActor") && Reject(c=>c.HeldItemToggle!.ItemId="../escape"),"toggle rejects unrelated source tags and unsafe item identities"));
        results.Add((Reject(c=>c.HeldItemToggle!.SourceInputEventTag="InputTag.Ability.TakeDown") && Reject(c=>c.HeldItemToggle!.PreserveNativeTakedown=false),
            "actor-local input bundles reject unscoped events and require native takedown priority"));
        var owner=CustomCharacterProjectService.CreateRecipe(null,"Example","Example","Example");owner.AbilityLoadout=profile;
        var child=CustomCharacterProjectService.CreateRecipe(owner,"Alternate","Example","Alternate",owner.SlotId);
        results.Add((child.AbilityLoadout!.HeldItemToggle!.SourceOwner=="Example" && child.AbilityLoadout.HeldItemToggle.Assets[0].Package==p.Assets[0].Package &&
            HeldItemToggleService.Root(HeldItemToggleService.Owner(owner),p)!=HeldItemToggleService.Root(HeldItemToggleService.Owner(child),child.AbilityLoadout.HeldItemToggle),
            "new suits retain immutable toggle sources but receive their own runtime asset namespace"));
        results.Add((HeldItemToggleService.InputEventTag(HeldItemToggleService.Owner(owner),p) != HeldItemToggleService.InputEventTag(HeldItemToggleService.Owner(child),child.AbilityLoadout.HeldItemToggle) &&
            HeldItemToggleService.TagRows(child).Any(t=>t.PawnTag==HeldItemToggleService.InputEventTag(HeldItemToggleService.Owner(child),child.AbilityLoadout.HeldItemToggle)),
            "rebased suits receive their own local input-event tag without changing the source bundle"));
        clone.HeldItems=[];HeldItemToggleService.Validate(clone);
        child.AbilityLoadout.HeldItemToggle.Enabled=false;
        results.Add((!HeldItemToggleService.TagRows(child).Any(),"disabled toggle permits normal held-item editing and emits no toggle gameplay tags"));
        var asset = new UAsset();
        asset.ClearNameIndexList();
        var leaf=new IntPropertyData(new FName(asset,"Value")){Value=1};
        var nested=new StructPropertyData(new FName(asset,"Entry")){Value=[new ArrayPropertyData(new FName(asset,"Layers")){Value=[leaf]}]};
        var copied=(StructPropertyData)HeldItemToggleService.DeepClone(nested);
        ((IntPropertyData)((ArrayPropertyData)copied.Value[0]).Value[0]).Value=2;
        results.Add((leaf.Value==1,"layer-table deep clone keeps the original armed entry independent from its unarmed variant"));
        var samples=JArray.FromObject(new[]{180f,-90f,0f,90f,-180f}.Select(x=>new{SampleValue=new{X=x}}));
        results.Add((HeldItemToggleService.ForwardSampleIndex(samples)==2,"unarmed forward movement patches zero degrees, not the 180-degree backward seam"));
        bool BadSamples(JArray rows){try{HeldItemToggleService.ForwardSampleIndex(rows);return false;}catch(InvalidDataException){return true;}}
        results.Add((BadSamples(new JArray(samples.Where(s=>s["SampleValue"]!["X"]!.Value<float>()!=0))) &&
            BadSamples(new JArray(samples.Concat(new[]{samples[2].DeepClone()}))),"unknown or ambiguous movement layouts fail closed instead of replacing another direction"));
        var table=new UAsset { Imports=[], Exports=[] };table.ClearNameIndexList();
        var classIndex=table.AddImport(new Import("/Script/CoreUObject","Class",FPackageIndex.FromRawIndex(0),"TTLayerSet",false,table));
        var sourceObj=SwordCombatService.Obj(table,prefix+"Armed", "Armed_C","/Script/Engine","AnimBlueprintGeneratedClass");
        var untouchedObj=SwordCombatService.Obj(table,prefix+"Other", "Other_C","/Script/Engine","AnimBlueprintGeneratedClass");
        StructPropertyData Row(FPackageIndex obj,int next,string[] tags)=>new(new FName(table,"Entry")){Value=[
            new StructPropertyData(new FName(table,"ContextTags")){Value=[new GameplayTagContainerPropertyData(new FName(table,"GameplayTags")){Value=tags.Select(t=>new FName(table,t)).ToArray()}]},
            new IntPropertyData(new FName(table,"ActionLink")){Value=next},
            new ArrayPropertyData(new FName(table,"LayerAnimArray")){Value=[new ObjectPropertyData(new FName(table,"Layer")){Value=obj}]}]};
        var originalRow=Row(sourceObj,1,[]);var otherRow=Row(untouchedObj,-1,["Animation.Status.Alert"]);
        var rows=new ArrayPropertyData(new FName(table,"AnimSetEntryArray")){Value=[originalRow,otherRow]};
        table.Exports.Add(new NormalExport(table,[]){ObjectName=new FName(table,"LayerSet"),ClassIndex=classIndex,Data=[rows]});
        var count=HeldItemToggleService.AddContextVariants(table,"Animation.Context.Example.Sheathed",new Dictionary<string,string>{{prefix+"Armed",prefix+"Sheathed"}},"AnimBlueprintGeneratedClass");
        var variant=(StructPropertyData)rows.Value[2];
        results.Add((count==1&&rows.Value.Length==3&&originalRow.Value.OfType<IntPropertyData>().Single().Value==2&&variant.Value.OfType<IntPropertyData>().Single().Value==1&&
            otherRow.Value.OfType<IntPropertyData>().Single().Value==-1&&((ObjectPropertyData)((ArrayPropertyData)originalRow.Value[2]).Value[0]).Value==sourceObj,
            "sheathed action/layer alternatives preserve armed references and the complete pre-existing selector chain"));
        return results;
    }
}
