using System.Security.Cryptography;
using System.Text.Json;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

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
        HeldItemToggleService.Validate(profile);
        var clone=AbilityExplorerForm.CloneProfile(profile);
        clone.HeldItemToggle!.Enabled=false;
        results.Add((p.Enabled && !clone.HeldItemToggle.Enabled && !ReferenceEquals(p.Assets,clone.HeldItemToggle.Assets),"ability editor deep-copies the complete private item-toggle bundle"));
        results.Add((AbilityLoadoutService.ConfigurationFingerprint(profile)!=AbilityLoadoutService.ConfigurationFingerprint(clone),"toggle enable/disable invalidates the saved ability build fingerprint"));
        bool Reject(Action<AbilityLoadoutProfile> edit) { var copy=AbilityExplorerForm.CloneProfile(profile);edit(copy);try { HeldItemToggleService.Validate(copy);return false; } catch { return true; } }
        results.Add((Reject(c=>c.HeldItems=[]) && Reject(c=>c.HeldItemToggle!.Assets.RemoveAt(0)),"toggle rejects a missing controlled prop or incomplete cooked closure"));
        results.Add((Reject(c=>c.HeldItemToggle!.Assets[0].UassetSha256="broken") && Reject(c=>c.HeldItemToggle!.Assets[0].Package="/Game/Characters/Native"),"toggle blocks corrupt payloads and native package replacements"));
        results.Add((Reject(c=>c.HeldItemToggle!.SourceRequestTag="Status.OtherActor") && Reject(c=>c.HeldItemToggle!.ItemId="../escape"),"toggle rejects unrelated source tags and unsafe item identities"));
        var owner=CustomCharacterProjectService.CreateRecipe(null,"Example","Example","Example");owner.AbilityLoadout=profile;
        var child=CustomCharacterProjectService.CreateRecipe(owner,"Alternate","Example","Alternate",owner.SlotId);
        results.Add((child.AbilityLoadout!.HeldItemToggle!.SourceOwner=="Example" && child.AbilityLoadout.HeldItemToggle.Assets[0].Package==p.Assets[0].Package &&
            HeldItemToggleService.Root(HeldItemToggleService.Owner(owner),p)!=HeldItemToggleService.Root(HeldItemToggleService.Owner(child),child.AbilityLoadout.HeldItemToggle),
            "new suits retain immutable toggle sources but receive their own runtime asset namespace"));
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
        return results;
    }
}
