using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Stages a user's cooked, portable per-avatar item toggle without installed overlays or native replacements.</summary>
internal static class HeldItemToggleService
{
    internal static string Owner(NativeSuitProject p) => p.TargetPackages.Playable.Split('/').ElementAtOrDefault(3) ?? "";
    internal static string Root(string owner, HeldItemToggleProfile p) => $"/Game/Mods/{owner}/HeldItemToggles/{p.ItemId}";
    internal static string Busy(string owner, HeldItemToggleProfile p) => $"Status.Batcomputer.ItemToggle.{owner}.{p.ItemId}.Busy";
    internal static string ContextTag(string owner, HeldItemToggleProfile p) => $"Animation.Context.Batcomputer.ItemToggle.{owner}.{p.ItemId}.Sheathed";
    internal static string InputEventTag(string owner, HeldItemToggleProfile p) => $"Event.Batcomputer.ItemToggle.{owner}.{p.ItemId}.Request";
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string MediaName(string owner, HeldItemToggleProfile p, string old) => "BCV_" + Hash(Encoding.UTF8.GetBytes(Root(owner,p)+"|"+old)).ToLowerInvariant();
    internal static Dictionary<string,string> Redirects(string owner, HeldItemToggleProfile p) => p.Assets.ToDictionary(a=>a.Package,a=>Root(owner,p)+"/Assets/"+UnrealPathUtil.AssetName(a.Package),StringComparer.Ordinal);
    internal static IEnumerable<PawnTagConfigService.TagRow> TagRows(NativeSuitProject project) => project.AbilityLoadout?.HeldItemToggle is { Enabled: true } p
        ? new[] { new PawnTagConfigService.TagRow(Busy(Owner(project),p),"Held-item toggle busy"),new(ContextTag(Owner(project),p),"Held-item sheathed animation context") }
            .Concat(p.Version >= 3 ? [new(InputEventTag(Owner(project),p),"Actor-local held-item input event")] : []) : [];
    internal static void Validate(AbilityLoadoutProfile loadout)
    {
        if (loadout.HeldItemToggle is not { Enabled: true } p) return;
        if (p.Version is not (1 or 2 or 3) || (p.PreserveNativeTakedown && p.Version < 2) || (p.Version == 3 && !p.PreserveNativeTakedown) || !Regex.IsMatch(p.SourceOwner,"^[A-Za-z][A-Za-z0-9_]{0,100}$") ||
            !Regex.IsMatch(p.ItemId,"^[A-Za-z0-9_]{1,64}$") || p.Assets.Count is < 2 or > 40 || p.Media.Count > 32)
            throw new InvalidDataException("Unsupported held-item toggle bundle or identity.");
        if (p.Version == 3 && p.SourceInputEventTag != InputEventTag(p.SourceOwner,p))
            throw new InvalidDataException("The input responder requires its character-local toggle event.");
        var item=loadout.HeldItems?.SingleOrDefault(i=>i.Id==p.ItemId);
        if (item is null || item.Visibility != HeldWeaponVisibility.Always) throw new InvalidDataException("The toggle requires its saved Always-held prop. Restore that prop or remove its toggle profile.");
        if (p.SourceRequestTag != $"Status.Batcomputer.HeldItem.{p.SourceOwner}.{p.ItemId}" ||
            !p.SourceBusyTag.StartsWith("Status.Batcomputer.",StringComparison.Ordinal) ||
            !p.SourceContextTag.StartsWith("Animation.Context.Batcomputer.",StringComparison.Ordinal)) throw new InvalidDataException("Invalid toggle source tags.");
        if (p.Assets.Select(a=>a.Package).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Assets.Count ||
            p.Assets.Select(a=>UnrealPathUtil.AssetName(a.Package)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=p.Assets.Count)
            throw new InvalidDataException("Toggle asset identities must be unique.");
        foreach(var a in p.Assets)
        {
            if (!a.Package.StartsWith("/Game/Mods/",StringComparison.Ordinal) || !HeldItemService.ValidPackage(a.Package))
                throw new InvalidDataException("Toggle assets must be owned by their source character.");
            foreach(var (encoded,sha) in new[]{(a.UassetBase64,a.UassetSha256),(a.UexpBase64,a.UexpSha256)})
                if (encoded.Length>16*1024*1024 || Hash(Convert.FromBase64String(encoded))!=sha) throw new InvalidDataException("Toggle asset hash mismatch.");
        }
        foreach(var required in new[]{p.ManagerPackage,p.TogglePackage,p.IdlePackage,p.WalkPackage,p.RunPackage,p.SprintPackage})
            if (!p.Assets.Any(a=>a.Package==required)) throw new InvalidDataException("Toggle bundle is missing an ability or unarmed animation.");
        if (!HeldItemService.ValidPackage(p.ReplacedAbility) || !HeldItemService.ValidPackage(p.RemovedFailureAbility)) throw new InvalidDataException("Invalid replaced input abilities.");
        if(p.SheathedAnimationReplacements.Count>64 || p.SheathedAnimationReplacements.Any(pair =>
            !HeldItemService.ValidPackage(pair.Key) || !HeldItemService.ValidPackage(pair.Value) || pair.Key==pair.Value ||
            (pair.Value.StartsWith("/Game/Mods/",StringComparison.Ordinal) && !p.Assets.Any(a=>a.Package==pair.Value))))
            throw new InvalidDataException("Sheathed animation pairs require exact packages and a complete portable replacement closure.");
        if (p.Media.Select(m=>m.Name).Distinct().Count()!=p.Media.Count) throw new InvalidDataException("Duplicate toggle audio media.");
        foreach(var m in p.Media)
        {
            if (!Regex.IsMatch(m.Name,"^BCV_[a-f0-9]{64}$") || m.WemBase64.Length>8*1024*1024 || Hash(Convert.FromBase64String(m.WemBase64))!=m.Sha256 || m.Mounts.Count is < 1 or > 40 ||
                m.Mounts.Any(path=>!Regex.IsMatch(path,@"^LEGOBatmanLotDK/(?:Content|Plugins/Wub_Loc_[a-z]{2}[A-Z]{2}/Content)/Wub/Platforms/Windows/Media/"+m.Name+@"\.wem$")))
                throw new InvalidDataException("Invalid private toggle audio payload or mount.");
        }
    }
    internal static CharacterVoiceBuildService.Result? Stage(NativeSuitProject project, string content)
    {
        if(project.AbilityLoadout?.HeldItemToggle is not { Enabled: true } p) return null;
        Validate(project.AbilityLoadout); var owner=Owner(project); var redirects=Redirects(owner,p);
        var mappings=MappingsCache.Load(AppSettings.Current.EffectiveUsmapPath()!);
        var renames=new Dictionary<string,string>(redirects,StringComparer.Ordinal) {
            [p.SourceRequestTag]=HeldItemService.RequestTag(owner,project.AbilityLoadout.HeldItems!.Single(i=>i.Id==p.ItemId)),
            [p.SourceBusyTag]=Busy(owner,p), [p.SourceContextTag]=ContextTag(owner,p)
        };
        if (p.Version >= 3) renames[p.SourceInputEventTag] = InputEventTag(owner,p);
        foreach(var m in p.Media) renames[m.Name]=MediaName(owner,p,m.Name);
        foreach(var a in p.Assets)
        {
            using var bytes=new MemoryStream(); bytes.Write(Convert.FromBase64String(a.UassetBase64)); bytes.Write(Convert.FromBase64String(a.UexpBase64)); bytes.Position=0;
            using var reader=new AssetBinaryReader(bytes);
            var asset=new UAsset(reader,EngineVersion.VER_UE5_6,mappings,true);
            if(asset.Imports.Any(i=>i.ObjectName.ToString().StartsWith("/Script/ClawAuthoring",StringComparison.Ordinal))) throw new InvalidDataException("Cook-only helper dependency in toggle bundle.");
            if (p.PreserveNativeTakedown && a.Package == p.TogglePackage &&
                (!asset.Imports.Any(i => i.ObjectName.ToString() == "Delay") ||
                 !asset.GetNameMapIndexList().Any(n => n.ToString() == "Abilities.Combat.TakeDownAttack")))
                throw new InvalidDataException("This toggle is missing its cooked native-takedown priority gate. Import a verified priority bundle.");
            if (p.Version >= 3 && a.Package == p.ManagerPackage &&
                (!asset.Imports.Any(i => i.ObjectName.ToString() == "WaitTagActionResponse") ||
                 !asset.Imports.Any(i => i.ObjectName.ToString() == "SendGameplayEventToActor") ||
                 !asset.Imports.Any(i => i.ObjectName.ToString() == "Delay") ||
                 !asset.GetNameMapIndexList().Any(n => n.ToString() == "InputTag.Ability.TakeDown") ||
                 !asset.GetNameMapIndexList().Any(n => n.ToString() == p.SourceInputEventTag)))
                throw new InvalidDataException("This toggle is missing its cooked actor-local Focus input responder.");
            if (p.Version >= 3 && a.Package == p.TogglePackage &&
                !asset.GetNameMapIndexList().Any(n => n.ToString() == p.SourceInputEventTag))
                throw new InvalidDataException("The toggle and input responder must use the same actor-local event.");
            var names=asset.GetNameMapIndexList();
            for(var i=0;i<names.Count;i++) if(renames.TryGetValue(names[i].ToString(),out var replacement)) asset.SetNameReference(i,new FString(replacement));
            asset.FolderName=new FString(redirects[a.Package]);
            // Only the explicit, identity-scoped bundle outputs are refreshed on rebuild.
            var file=FileFor(content,redirects[a.Package]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); asset.Write(file);
        }
        var stage=Directory.GetParent(content)!.FullName; // LEGOBatmanLotDK
        var outer=Directory.GetParent(stage)!.FullName;
        var files=new List<string>(); var mounts=new List<string>();
        foreach(var m in p.Media) foreach(var mount in m.Mounts)
        {
            var targetMount=mount.Replace(m.Name,MediaName(owner,p,m.Name),StringComparison.Ordinal);
            var file=Path.GetFullPath(Path.Combine(outer,targetMount.Replace('/',Path.DirectorySeparatorChar)));
            if(!FileSystemPathUtil.IsWithinDirectory(file,outer)) throw new InvalidDataException("Toggle audio escaped stage.");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllBytes(file,PrivateWemSetupService.Normalize(Convert.FromBase64String(m.WemBase64))); files.Add(file); mounts.Add(targetMount);
        }
        var report=Path.Combine(outer,"held-item-toggle-media.json");
        File.WriteAllText(report,JsonSerializer.Serialize(new { Packages=redirects.Values, MediaMountPaths=mounts }));
        return new("",redirects.Values.ToArray(),files.ToArray(),report,mounts.ToArray());
    }
    private static string FileFor(string content,string package)
    {
        if(!package.StartsWith("/Game/Mods/",StringComparison.Ordinal) || !HeldItemService.ValidPackage(package)) throw new InvalidDataException("Toggle writes must be mod-local.");
        var path=Path.GetFullPath(Path.Combine(content,package[6..].Replace('/',Path.DirectorySeparatorChar))+".uasset");
        if(!FileSystemPathUtil.IsWithinDirectory(path,content)) throw new InvalidDataException("Toggle output escaped stage."); return path;
    }
    private static IEnumerable<PropertyData> Walk(IEnumerable<PropertyData> props)
    { foreach(var p in props) { yield return p; foreach(var c in Walk(p is StructPropertyData s?s.Value:p is ArrayPropertyData a?a.Value:[])) yield return c; } }
    internal static PropertyData DeepClone(PropertyData p)
    {
        var copy=(PropertyData)p.Clone();
        if(p is StructPropertyData s && copy is StructPropertyData sc) sc.Value=s.Value.Select(DeepClone).ToList();
        if(p is ArrayPropertyData a && copy is ArrayPropertyData ac) ac.Value=a.Value.Select(DeepClone).ToArray();
        return copy;
    }
    internal static void Generate(NativeSuitProject project,string extracted,string content,string owner,string dprd,Usmap mappings,List<string> log)
    {
        if(project.AbilityLoadout?.HeldItemToggle is not { Enabled: true } p) return;
        Validate(project.AbilityLoadout); var root=Root(owner,p); var redirect=Redirects(owner,p);
        Stage(project,content);
        using var c=new SwordCombatService.Context(extracted,content,owner,mappings,root);
        var mutation=new AbilityAssetMutationService(); var before=mutation.InspectDprdAbilitySets(dprd);
        if(!before.Success) throw new InvalidDataException(before.Error);
        var sets=before.AbilitySets.Select(s=>s.PackagePath).ToList(); var count=0;
        for(var i=0;i<sets.Count;i++)
        {
            var file=FileForOrNative(content,extracted,sets[i]); var inspected=mutation.InspectAbilitySet(file);
            if(!inspected.Success) throw new InvalidDataException(inspected.Error);
            var edits=InputEdits(inspected,p,redirect[p.TogglePackage]);
            if(edits.Length==0) continue;
            count+=inspected.GameplayAbilities.Count(g=>g.PackagePath==p.ReplacedAbility);
            var to=root+"/AS_Input_"+i; c.Clone(sets[i],to);
            var changed=mutation.ApplyGameplayAbilityEdits(c.PathFor(to),edits); if(!changed.Success) throw new InvalidDataException(changed.Error); sets[i]=to;
        }
        if(count!=1) throw new InvalidDataException("The held-item toggle must replace exactly one saved Focus ability. Restore/remap that input before building.");
        var item=project.AbilityLoadout.HeldItems!.Single(i=>i.Id==p.ItemId);
        var set=c.Clone(HeldItemService.SetPackage(owner,item),root+"/AS_Manager");
        var data=set.Exports.OfType<NormalExport>().Single(e=>e.GetExportClassType()?.ToString()=="TtAbilitySet");
        var grant=(StructPropertyData)data.Data.OfType<ArrayPropertyData>().Single(a=>a.Name.ToString()=="GrantedGameplayAbilities").Value.Single();
        var ga=SwordCombatService.Obj(set,redirect[p.ManagerPackage],UnrealPathUtil.AssetName(p.ManagerPackage)+"_C","/Script/Engine","BlueprintGeneratedClass");
        grant.Value.OfType<ObjectPropertyData>().Single(a=>a.Name.ToString()=="Ability").Value=ga; data.CreateBeforeSerializationDependencies.Add(ga); c.Write(set,root+"/AS_Manager"); sets.Add(root+"/AS_Manager");
        var result=mutation.SetDprdAbilitySets(dprd,sets); if(!result.Success) throw new InvalidDataException(result.Error);
        using var provider=ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(),AppSettings.Current.EffectiveUsmapPath()!,[content]);
        JObject Properties(string package) => JArray.FromObject(provider.LoadPackage(package).GetExports()).OfType<JObject>().Single(e=>e["Name"]?.ToString().StartsWith("Default__",StringComparison.Ordinal)==true)["Properties"]!.ToObject<JObject>()!;
        string Reference(JToken value) => value["ObjectPath"]!.ToString().Replace("LEGOBatmanLotDK/Content/","/Game/",StringComparison.Ordinal).Split('.')[0];
        var statePairs=p.SheathedAnimationReplacements.ToDictionary(pair=>pair.Key,pair=>redirect.GetValueOrDefault(pair.Value,pair.Value),StringComparer.Ordinal);
        var chars=$"/Game/Mods/{owner}/Characters/";
        var corePackage=chars+"ABP_Core_"+owner; var coreProps=Properties(corePackage);
        var core=c.Clone(corePackage,root+"/ABP_Core_Unarmed",new Dictionary<string,string>(StringComparer.Ordinal));
        var coreRefs=new[]{coreProps["Idle_AnimSequence"]}.Concat(coreProps["Idle2_AnimSequences"]?.Children()??[]).Where(v=>v!=null).Select(v=>Reference(v!)).Distinct().ToArray();
        var coreRedirects=new Dictionary<string,string>(statePairs,StringComparer.Ordinal);
        foreach(var reference in coreRefs) coreRedirects[reference]=redirect[p.IdlePackage];
        Repoint(core,coreRedirects); c.Write(core,root+"/ABP_Core_Unarmed");
        var movementPackage=chars+"ABP_Movement_"+owner; var moveProps=Properties(movementPackage);
        var movement=c.Clone(movementPackage,root+"/ABP_Movement_Unarmed"); var movementRedirects=new Dictionary<string,string>();
        foreach(var (name,clip) in new[]{("Walk",p.WalkPackage),("Run",p.RunPackage)})
        {
            var original=Reference(moveProps[name+"_BlendSpace"]!); var to=root+"/BS_"+name+"_Unarmed"; var bs=c.Clone(original,to);
            var samples=JArray.FromObject(provider.LoadPackage(original).GetExports())[0]!["Properties"]!["SampleData"]!;
            // These native 1D spaces use travel angle, not speed: zero is forward;
            // the greatest positive value is the duplicated backward seam at 180 degrees.
            var index=ForwardSampleIndex((JArray)samples);
            var rows=bs.Exports.OfType<NormalExport>().Single(e=>e.GetExportClassType()?.ToString()=="BlendSpace1D").Data.OfType<ArrayPropertyData>().Single(a=>a.Name.ToString()=="SampleData");
            var sample=(StructPropertyData)rows.Value[index]; var field=sample.Value.OfType<ObjectPropertyData>().Single(a=>a.Name.ToString()=="Animation");
            var obj=SwordCombatService.Obj(bs,redirect[clip],UnrealPathUtil.AssetName(clip),"/Script/Engine","AnimSequence"); field.Value=obj;
            bs.Exports[0].CreateBeforeSerializationDependencies.Add(obj); Repoint(bs,statePairs); c.Write(bs,to); movementRedirects[original]=to;
        }
        movementRedirects[Reference(moveProps["Sprint_AnimSequence"]!)]=redirect[p.SprintPackage]; Repoint(movement,movementRedirects); c.Write(movement,root+"/ABP_Movement_Unarmed");
        var lasFile=FileFor(content,chars+"LAS_Default_"+owner); var las=new UAsset(lasFile,EngineVersion.VER_UE5_6,mappings);
        var layer=las.Exports.OfType<NormalExport>().Single(e=>e.GetExportClassType()?.ToString()=="TTLayerSet"); var entries=layer.Data.OfType<ArrayPropertyData>().Single(a=>a.Name.ToString()=="AnimSetEntryArray");
        var layerRedirects=new Dictionary<string,string>{[corePackage]=root+"/ABP_Core_Unarmed",[movementPackage]=root+"/ABP_Movement_Unarmed"};
        foreach(var original in Walk(entries.Value).OfType<ObjectPropertyData>().Where(o=>o.Value.IsImport()).Select(o=>{
            var import=las.Imports[-o.Value.Index-1];return import.OuterIndex.IsImport()?las.Imports[-import.OuterIndex.Index-1].ObjectName.ToString():"";
        }).Where(pkg=>pkg.StartsWith(chars,StringComparison.Ordinal)&&!layerRedirects.ContainsKey(pkg)).Distinct().ToArray())
        {
            var source=new UAsset(FileFor(content,original),EngineVersion.VER_UE5_6,mappings);
            if(!source.GetNameMapIndexList().Any(n=>statePairs.ContainsKey(n.ToString())))continue;
            var target=root+"/"+UnrealPathUtil.AssetName(original)+"_Sheathed";
            var copy=c.Clone(original,target);Repoint(copy,statePairs);c.Write(copy,target);layerRedirects[original]=target;
        }
        AddContextVariants(las,ContextTag(owner,p),layerRedirects,"AnimBlueprintGeneratedClass");las.Write(lasFile);
        var setsDirectory=Path.Combine(content,"Mods",owner,"Characters","AnimationSets");
        if(Directory.Exists(setsDirectory))foreach(var file in Directory.GetFiles(setsDirectory,"*.uasset",SearchOption.TopDirectoryOnly))
        {
            var setAsset=new UAsset(file,EngineVersion.VER_UE5_6,mappings);
            if(!setAsset.Exports.OfType<NormalExport>().Any(e=>e.GetExportClassType()?.ToString()=="TTAnimSet"))continue;
            if(AddContextVariants(setAsset,ContextTag(owner,p),statePairs,"AnimMontage")>0)setAsset.Write(file);
        }
        log.Add("Held-item toggle rebuilt: private Focus binding" + (p.PreserveNativeTakedown ? " with native takedown priority" : "") + (p.Version >= 3 ? " and an actor-local input responder" : "") + ", native audio, auto-extension and separate unarmed idle/forward movement. Physical deployment remains visibility-only.");
    }
    internal static int ForwardSampleIndex(JArray samples)
    {
        var forward=samples.Select((sample,index)=>(sample,index))
            .Where(row => row.sample["SampleValue"]?["X"] is { } x && Math.Abs(x.Value<float>()) < 0.0001f).ToArray();
        if(forward.Length!=1) throw new InvalidDataException("The held-item movement adapter requires exactly one zero-angle forward sample.");
        return forward[0].index;
    }
    internal static int AddContextVariants(UAsset asset,string tag,IReadOnlyDictionary<string,string> replacements,string assetClass)
    {
        var export=asset.Exports.OfType<NormalExport>().Single(e=>e.GetExportClassType()?.ToString() is "TTLayerSet" or "TTAnimSet");
        var entries=export.Data.OfType<ArrayPropertyData>().Single(a=>a.Name.ToString()=="AnimSetEntryArray");var added=new List<PropertyData>();
        foreach(var entry in entries.Value.Cast<StructPropertyData>())
        {
            var copy=(StructPropertyData)DeepClone(entry);var changed=false;
            foreach(var obj in Walk(copy.Value).OfType<ObjectPropertyData>().Where(o=>o.Value.IsImport()))
            {
                var import=asset.Imports[-obj.Value.Index-1];if(!import.OuterIndex.IsImport())continue;
                var package=asset.Imports[-import.OuterIndex.Index-1].ObjectName.ToString();
                if(!replacements.TryGetValue(package,out var target))continue;
                if(import.ClassName.ToString()!=assetClass)throw new InvalidDataException("Sheathed animation pair has the wrong reference class: "+package);
                var targetObj=SwordCombatService.Obj(asset,target,UnrealPathUtil.AssetName(target)+(assetClass=="AnimBlueprintGeneratedClass"?"_C":""),"/Script/Engine",assetClass);
                obj.Value=targetObj;export.CreateBeforeSerializationDependencies.Add(targetObj);changed=true;
            }
            if(!changed)continue;
            var context=copy.Value.OfType<StructPropertyData>().Single(p=>p.Name.ToString()=="ContextTags").Value.OfType<GameplayTagContainerPropertyData>().Single();
            context.Value=[..context.Value,new FName(asset,tag)];
            // Insert after this selector, retaining its old next link on the copy.
            // More-specific sheathed contexts must not disconnect later variants.
            entry.Value.OfType<IntPropertyData>().Single(p=>p.Name.ToString()=="ActionLink").Value=entries.Value.Length+added.Count;added.Add(copy);
        }
        entries.Value=[..entries.Value,..added];return added.Count;
    }
    internal static AbilityAssetMutationService.GameplayAbilityEdit[] InputEdits(AbilityAssetMutationService.AbilitySetInspection source, HeldItemToggleProfile p, string toggle)
    {
        var result = source.GameplayAbilities.Where(g => g.PackagePath == p.RemovedFailureAbility).Select(g => new AbilityAssetMutationService.GameplayAbilityEdit {
            Kind = AbilityAssetMutationService.GameplayAbilityEditKind.Remove, TargetPackagePath = g.PackagePath }).ToList();
        foreach (var native in source.GameplayAbilities.Where(g => g.PackagePath == p.ReplacedAbility))
            result.Add(new() { Kind = p.PreserveNativeTakedown ? AbilityAssetMutationService.GameplayAbilityEditKind.Add : AbilityAssetMutationService.GameplayAbilityEditKind.Replace,
                TargetPackagePath = p.PreserveNativeTakedown ? toggle : native.PackagePath, ReplacementPackagePath = p.PreserveNativeTakedown ? "" : toggle,
                AbilityLevelOverride = native.AbilityLevel, InputTagOverride = native.InputTag,
                SourceAbilityPackagePath = native.PackagePath });
        return result.ToArray();
    }
    private static void Repoint(UAsset asset,Dictionary<string,string> redirects)
    {
        var identities=new Dictionary<string,string>(redirects,StringComparer.Ordinal);
        foreach(var (from,to) in redirects) identities[UnrealPathUtil.AssetName(from)]=UnrealPathUtil.AssetName(to);
        var names=asset.GetNameMapIndexList();
        for(var i=0;i<names.Count;i++) if(identities.TryGetValue(names[i].ToString(),out var target)) asset.SetNameReference(i,new FString(target));
    }
    private static string FileForOrNative(string content,string extracted,string package)
    {
        foreach (var root in new[] { content, extracted })
        {
            var file = ExtractedPackagePathService.ResolvePackageUasset(root, package);
            if (file != null && File.Exists(file)) return file;
        }
        throw new FileNotFoundException("Missing toggle source AbilitySet: " + package);
    }
    internal static List<string> VerifySets(NativeSuitProject project,string extracted,string content,string owner,IReadOnlyList<string> original,Usmap maps)
    {
        if(project.AbilityLoadout?.HeldItemToggle is not { Enabled: true } p) return original.ToList();
        Validate(project.AbilityLoadout); var root=Root(owner,p); var redirects=Redirects(owner,p); var result=original.ToList(); var mutation=new AbilityAssetMutationService(); var replaced=0;
        for(var i=0;i<original.Count;i++)
        {
            var source=mutation.InspectAbilitySet(FileForOrNative(content,extracted,original[i])); if(!source.Success) throw new InvalidDataException(source.Error);
            if(!source.GameplayAbilities.Any(g=>g.PackagePath==p.ReplacedAbility || g.PackagePath==p.RemovedFailureAbility)) continue;
            var expected=source.GameplayAbilities.Where(g=>g.PackagePath!=p.RemovedFailureAbility).ToList();
            if (p.PreserveNativeTakedown)
                expected.AddRange(source.GameplayAbilities.Where(g => g.PackagePath == p.ReplacedAbility).Select(g => new AbilityAssetMutationService.AbilityGrantReference {
                    PackagePath = redirects[p.TogglePackage], AbilityLevel = g.AbilityLevel, InputTag = g.InputTag }));
            var pkg=root+"/AS_Input_"+i; var actual=mutation.InspectAbilitySet(FileFor(content,pkg));
            if(!actual.Success || actual.GameplayAbilities.Count != expected.Count ||
                expected.Where((g,j) => actual.GameplayAbilities[j].PackagePath != (g.PackagePath==p.ReplacedAbility && !p.PreserveNativeTakedown?redirects[p.TogglePackage]:g.PackagePath) ||
                    actual.GameplayAbilities[j].AbilityLevel != g.AbilityLevel || actual.GameplayAbilities[j].InputTag != g.InputTag).Any())
                throw new InvalidDataException("Toggle input grants or their binding metadata changed unexpectedly.");
            foreach(var (before,after) in new[] {
                (source.GameplayEffects,actual.GameplayEffects), (source.Attributes,actual.Attributes),
                (source.GameplayData,actual.GameplayData), (source.ActorGameplayCues,actual.ActorGameplayCues),
                (source.StaticGameplayCues,actual.StaticGameplayCues) })
                if(JsonSerializer.Serialize(before) != JsonSerializer.Serialize(after))
                    throw new InvalidDataException("Toggle input wrapper changed unrelated ability-set data.");
            if(JsonSerializer.Serialize(source.AccessoryAnimGraphClass) != JsonSerializer.Serialize(actual.AccessoryAnimGraphClass))
                throw new InvalidDataException("Toggle input wrapper changed the accessory animation graph.");
            replaced+=source.GameplayAbilities.Count(g=>g.PackagePath==p.ReplacedAbility); result[i]=pkg;
        }
        var manager=root+"/AS_Manager"; var grant=mutation.InspectAbilitySet(FileFor(content,manager));
        if(replaced!=1 || !grant.Success || !grant.GameplayAbilities.Select(g=>g.PackagePath).SequenceEqual(new[]{redirects[p.ManagerPackage]})) throw new InvalidDataException("Toggle manager/input grant mismatch.");
        result.Add(manager); return result;
    }
}
