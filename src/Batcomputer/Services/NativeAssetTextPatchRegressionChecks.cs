using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.UnrealTypes;

namespace Batcomputer;

internal static class NativeAssetTextPatchRegressionChecks
{
    internal static IEnumerable<(bool Passed, string Description)> Run()
    {
        static UAsset Fixture(string className)
        {
            var asset = new UAsset { Imports = [], Exports = [] };
            asset.ClearNameIndexList();
            asset.Exports.Add(new NormalExport(asset, [])
            {
                ObjectName = FName.FromString(asset, "Fixture"), Data = [],
                ClassIndex = SwordCombatService.Obj(asset, "/Script/Test", className, "/Script/CoreUObject", "Class")
            });
            return asset;
        }
        const string table = "/Game/Mods/Test/ST_Test.ST_Test";
        var ui = Fixture("TtPawnUIMetaData");
        yield return (NativeAssetTextPatch.SetStringTableText(ui, "Description", table, "Description") &&
            NativeAssetTextPatch.SetStringTableText(ui, "LockedDescription", table, "Locked"),
            "native pawn UI templates with omitted descriptions accept custom localized text");
        var data = ((NormalExport)ui.Exports[0]).Data;
        yield return (NativeAssetTextPatch.SetStringTableText(ui, "Description", table, "Updated") && data.Count == 2 &&
            data.OfType<TextPropertyData>().Single(p => p.Name.ToString() == "Description").Value.ToString() == "Updated",
            "repatching a default-omitted UI description updates it without duplicate fields");
        yield return (!NativeAssetTextPatch.SetStringTableText(ui, "NotAField", table, "No") && data.Count == 2 &&
            !NativeAssetTextPatch.SetStringTableText(Fixture("TtEquipmentTaggedAsset"), "Description", table, "No"),
            "missing text insertion remains restricted to known pawn UI description fields");
        var malformed = Fixture("TtPawnUIMetaData");
        ((NormalExport)malformed.Exports[0]).Data.Add(new NamePropertyData(FName.FromString(malformed, "Description"))
            { Value = FName.FromString(malformed, "WrongType") });
        yield return (!NativeAssetTextPatch.SetStringTableText(malformed, "Description", table, "No"),
            "wrongly typed native UI descriptions fail closed rather than being replaced");
    }
}
