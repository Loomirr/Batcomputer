using System.Security.Cryptography;
using System.Text;
using UAssetAPI.Unversioned;

namespace Batcomputer;

/// <summary>Replace one glide category without flattening costume-specific composite inheritance.</summary>
internal static class PairedCapeAnimationGraphService
{
    private const string GlideAction = "Animation.Layer.Ability.Glide";
    private static bool PreserveNativeTraversal(Route route, string prefix) => prefix == "LAS_Traversal_" &&
        route.Category is "/Game/AdditionalContent/VillainMode/Animations/LayerAnimSets/Traversal/LAS_Traversal_Joker" or
            "/Game/AdditionalContent/VillainMode/Animations/LayerAnimSets/Traversal/LAS_Traversal_HarleyQuinn";
    internal sealed record Route(IReadOnlyList<string> Owners, string Category);

    internal static Route FindRoute(string root, string prefix, string preferred, bool allowMissing,
        Func<string, AnimGraftService.ParentSetInspection> read)
    {
        var routes = new List<Route>();
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compositePrefix = prefix.StartsWith("MAS_", StringComparison.Ordinal) ? "MAS_Char_" : "LAS_Char_";
        void Visit(string package, List<string> path)
        {
            if (path.Count >= 32 || !active.Add(package))
                throw new InvalidDataException("Cyclic or excessively deep character animation inheritance: " + package);
            var inspection = read(package);
            if (!inspection.Success) throw new InvalidDataException(inspection.Error ?? "Unreadable character animation set: " + package);
            var owners = new List<string>(path) { package };
            routes.AddRange(inspection.PackagePaths.Where(p => UnrealPathUtil.AssetName(p).StartsWith(prefix, StringComparison.Ordinal))
                .Select(p => new Route(owners, p)));
            foreach (var parent in inspection.PackagePaths.Where(p => UnrealPathUtil.AssetName(p).StartsWith(compositePrefix, StringComparison.Ordinal)))
                Visit(parent, owners);
            active.Remove(package);
        }
        Visit(root, []);
        var exact = routes.Where(r => UnrealPathUtil.AssetName(r.Category).Equals(preferred, StringComparison.Ordinal)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count == 0 && routes.Count == 1) return routes[0];
        if (allowMissing && routes.Count == 0) return new([root], "");
        throw new InvalidDataException($"The paired-cape donor must expose one unambiguous {prefix} category through its character composites (found {routes.Count}, preferred {exact.Count}).");
    }

    private static string NativeFile(string package) => ExtractedPackagePathService.ResolvePackageUasset(
        AppSettings.Current.EffectiveExtractedContentRoot(), package)
        ?? throw new InvalidDataException("Missing native animation composite: " + package);

    internal static Route NativeRoute(string source, string prefix, string preferred, bool allowMissing) =>
        FindRoute(source, prefix, preferred, allowMissing, p => new AnimGraftService().InspectParentSets(NativeFile(p)));

    internal static string ClonePackage(string generatedRoot, string source)
    {
        var directory = generatedRoot[..generatedRoot.LastIndexOf('/')];
        var kind = UnrealPathUtil.AssetName(generatedRoot).StartsWith("MAS_", StringComparison.Ordinal) ? "MAS" : "LAS";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()[..12];
        return directory + "/" + kind + "_CapeParent_" + UnrealPathUtil.AssetName(generatedRoot) + "_" + hash;
    }

    internal static void Apply(string nativeRoot, string generatedRoot, string stagedRoot, string certified,
        string prefix, string preferred, bool allowMissing, Usmap? mappings, AnimArchetypeGraftService.Result log)
    {
        var route = NativeRoute(nativeRoot, prefix, preferred, allowMissing);
        var graft = new AnimGraftService();
        var current = generatedRoot;
        string FileFor(string package) => ExtractedPackagePathService.ResolvePackageUasset(stagedRoot, package)
            ?? throw new InvalidDataException("Missing staged cape animation composite: " + package);
        for (var i = 1; i < route.Owners.Count; i++)
        {
            var source = route.Owners[i];
            var clone = ClonePackage(generatedRoot, source);
            AnimArchetypeGraftService.CloneDonorAsset(AppSettings.Current.EffectiveExtractedContentRoot(), source,
                UnrealPathUtil.AssetName(source), stagedRoot, clone, UnrealPathUtil.AssetName(clone), mappings, log);
            var before = graft.InspectParentSets(FileFor(current));
            if (!before.Success || before.PackagePaths.Count(p => p == source) != 1)
                throw new InvalidDataException("The inherited cape animation edge is absent or ambiguous.");
            AnimArchetypeGraftService.ApplyNameMapExact(FileFor(current), new()
                { [source] = clone, [UnrealPathUtil.AssetName(source)] = UnrealPathUtil.AssetName(clone) }, mappings);
            var after = graft.InspectParentSets(FileFor(current));
            if (!after.Success || !before.PackagePaths.Select(p => p == source ? clone : p).SequenceEqual(after.PackagePaths))
                throw new InvalidDataException("The inherited cape animation edge did not survive serialization.");
            current = clone;
        }
        var kind = prefix.StartsWith("MAS_") ? "TTAnimSet" : "TTLayerSet";
        if (PreserveNativeTraversal(route, prefix))
        {
            // Native traversal sets also contain grapple, hover, balance and stealth rows.
            // Keep those verbatim. Removing only the non-cape Glide override lets the authored
            // cape scaffold use its native glide controller; the certified cape block stays active.
            var privateTraversal = ClonePackage(generatedRoot, route.Category);
            AnimArchetypeGraftService.CloneDonorAsset(AppSettings.Current.EffectiveExtractedContentRoot(), route.Category,
                UnrealPathUtil.AssetName(route.Category), stagedRoot, privateTraversal, UnrealPathUtil.AssetName(privateTraversal), mappings, log);
            var filtered = graft.RemoveLayerActions(FileFor(privateTraversal), [GlideAction]);
            if (filtered.Status != "ok") throw new InvalidDataException(filtered.Error);
            AnimGraftService.VerifyLayerActionsPreserved(NativeFile(route.Category), FileFor(privateTraversal), [GlideAction]);
            var replaced = graft.ReplaceParentSet(FileFor(current), kind, UnrealPathUtil.AssetName(route.Category), privateTraversal, requireExisting: true);
            if (replaced.Status != "ok") throw new InvalidDataException(replaced.Error);
            var appended = graft.InjectParentSets(FileFor(current), kind, [certified]);
            if (appended.Status != "ok") throw new InvalidDataException(appended.Error);
            log.Log.Add("paired-cape native traversal: retained every non-glide action in " + privateTraversal);
            return;
        }
        var result = route.Category.Length == 0
            ? graft.InjectParentSets(FileFor(current), kind, [certified])
            : graft.ReplaceParentSet(FileFor(current), kind, UnrealPathUtil.AssetName(route.Category), certified, requireExisting: true);
        if (result.Status != "ok") throw new InvalidDataException(result.Error ?? "Could not replace the inherited cape animation category.");
        log.Log.Add($"paired-cape {prefix} bridge: {route.Owners.Count} composite(s), {route.Category} -> {certified}");
    }

    internal static void Verify(string nativeRoot, string generatedRoot, string stagedRoot, string certified,
        string prefix, string preferred, bool allowMissing)
    {
        var route = NativeRoute(nativeRoot, prefix, preferred, allowMissing);
        var graft = new AnimGraftService();
        for (var i = 0; i < route.Owners.Count; i++)
        {
            var source = graft.InspectParentSets(NativeFile(route.Owners[i]));
            var generated = i == 0 ? generatedRoot : ClonePackage(generatedRoot, route.Owners[i]);
            var file = ExtractedPackagePathService.ResolvePackageUasset(stagedRoot, generated)
                ?? throw new InvalidDataException("Missing private inherited cape composite: " + generated);
            var actual = graft.InspectParentSets(file);
            if (!actual.Success || actual.AuthoredNullEntries != source.AuthoredNullEntries)
                throw new InvalidDataException(actual.Error ?? "The cape composite lost authored null parent slots.");
            if (i + 1 == route.Owners.Count)
            {
                if (PreserveNativeTraversal(route, prefix))
                {
                    var privateTraversal = ClonePackage(generatedRoot, route.Category);
                    var expected = source.PackagePaths.Select(p => p == route.Category ? privateTraversal : p);
                    if (actual.PackagePaths.Count(p => p.Equals(certified, StringComparison.OrdinalIgnoreCase)) != 1 ||
                        !expected.SequenceEqual(actual.PackagePaths.Where(p => !p.Equals(certified, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("The cape layer must retain the private native traversal copy and the exact certified cape block.");
                    var layerFile = ExtractedPackagePathService.ResolvePackageUasset(stagedRoot, privateTraversal)
                        ?? throw new InvalidDataException("Missing private native traversal layer: " + privateTraversal);
                    AnimGraftService.VerifyLayerActionsPreserved(NativeFile(route.Category), layerFile, [GlideAction]);
                    continue;
                }
                if (!StageValidationService.PairedCapeAnimationParentsAreSafeForTest(source.PackagePaths, actual.PackagePaths,
                        certified, prefix, out var detail, allowMissing, preferred))
                    throw new InvalidDataException(detail);
            }
            else
            {
                var next = route.Owners[i + 1];
                var clone = ClonePackage(generatedRoot, next);
                var expected = source.PackagePaths.Select(p => p == next ? clone : p).ToList();
                if (!expected.SequenceEqual(actual.PackagePaths, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("The inherited cape bridge lost or reordered costume-specific animation parents: " + generated);
                // These ancestor wrappers need only a name-map edge rewrite; their authored
                // rows (including alternate costume menus) must remain byte-for-byte intact.
                if (!File.ReadAllBytes(Path.ChangeExtension(NativeFile(route.Owners[i]), ".uexp"))
                        .SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(file, ".uexp"))))
                    throw new InvalidDataException("The inherited cape bridge changed the costume wrapper's cooked animation rows.");
            }
        }
    }
}
