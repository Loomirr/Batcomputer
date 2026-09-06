#include "BatcomputerRegistryWriterCommandlet.h"

#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetBundleData.h"
#include "AssetRegistry/AssetRegistryState.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/ArrayWriter.h"
#include "UObject/PrimaryAssetId.h"

// Keep compiler-provided file names portable in the prebuilt release module.
// UE_LOG expands __FILE__ into the binary, so without this mapping the helper
// would retain the local account path used to produce the official build.
#line 1 "BatcomputerRegistryWriterCommandlet.cpp"

DEFINE_LOG_CATEGORY_STATIC(LogBatcomputerRegistryWriter, Log, All);

namespace
{
struct FAdditionalRegistryRow
{
    FString PackageName;
    FString PrimaryAssetName;
    FString PrimaryAssetType;
    FString ClassPathText;
    FTopLevelAssetPath AssetClassPath;
};

bool BundlesMatch(const FAssetBundleData& Actual, const FAssetBundleData& Expected)
{
    // Cooked registry serialization sorts bundle names and asset paths. Compare
    // exact membership, not insertion order (the equipment slot order lives in DCMD).
    if (Actual.Bundles.Num() != Expected.Bundles.Num()) return false;
    for (const FAssetBundleEntry& Entry : Expected.Bundles)
    {
        const FAssetBundleEntry* Found = Actual.Bundles.FindByPredicate(
            [&](const FAssetBundleEntry& Candidate) { return Candidate.BundleName == Entry.BundleName; });
        if (!Found || Found->AssetPaths.Num() != Entry.AssetPaths.Num()) return false;
        for (const FTopLevelAssetPath& Path : Entry.AssetPaths)
        {
            if (!Found->AssetPaths.Contains(Path)) return false;
        }
    }
    return true;
}

bool ReadRequiredValue(
    const FString& Params,
    const TCHAR* Key,
    FString& OutValue)
{
    if (!FParse::Value(*Params, Key, OutValue))
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("Missing required argument %s<value>"),
            Key);
        return false;
    }

    OutValue.TrimQuotesInline();
    OutValue.TrimStartAndEndInline();
    if (OutValue.IsEmpty())
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("Argument %s must not be empty"),
            Key);
        return false;
    }
    return true;
}
}

UBatcomputerRegistryWriterCommandlet::UBatcomputerRegistryWriterCommandlet()
{
    IsClient = false;
    IsEditor = true;
    IsServer = false;
    LogToConsole = true;
    ShowErrorCount = true;
}

int32 UBatcomputerRegistryWriterCommandlet::Main(const FString& Params)
{
    FString OutputPath;
    FString PackageName;
    FString ClassPathText;
    FString PrimaryAssetType;
    FString PrimaryAssetName;
    FString SentinelPackageName;
    FString AdditionalRowsText;

    if (!ReadRequiredValue(Params, TEXT("Output="), OutputPath) ||
        !ReadRequiredValue(Params, TEXT("Package="), PackageName) ||
        !ReadRequiredValue(Params, TEXT("Class="), ClassPathText) ||
        !ReadRequiredValue(Params, TEXT("PrimaryAssetType="), PrimaryAssetType) ||
        !ReadRequiredValue(Params, TEXT("PrimaryAssetName="), PrimaryAssetName))
    {
        return 2;
    }

    const bool bWriteSentinel =
        FParse::Value(*Params, TEXT("SentinelPackage="), SentinelPackageName);
    if (bWriteSentinel)
    {
        SentinelPackageName.TrimQuotesInline();
        SentinelPackageName.TrimStartAndEndInline();
        if (SentinelPackageName.IsEmpty() ||
            !FPackageName::IsValidLongPackageName(SentinelPackageName))
        {
            UE_LOG(
                LogBatcomputerRegistryWriter,
                Error,
                TEXT("Invalid SentinelPackage long package name: %s"),
                *SentinelPackageName);
            return 3;
        }
    }

    if (!FPackageName::IsValidLongPackageName(PackageName))
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("Invalid long package name: %s"),
            *PackageName);
        return 3;
    }

    FString ClassPackageName;
    FString ClassAssetName;
    if (!ClassPathText.Split(
            TEXT("."),
            &ClassPackageName,
            &ClassAssetName,
            ESearchCase::CaseSensitive,
            ESearchDir::FromEnd) ||
        ClassPackageName.IsEmpty() ||
        ClassAssetName.IsEmpty())
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("Class must be a top-level object path such as /Script/Module.Class: %s"),
            *ClassPathText);
        return 4;
    }

    const FString AssetName = FPackageName::GetShortName(PackageName);
    const FString ObjectPath = PackageName + TEXT(".") + AssetName;
    const FTopLevelAssetPath AssetClassPath{
        FName(*ClassPackageName),
        FName(*ClassAssetName)};

    // Legacy suit rows may omit the last two fields. New rows always carry
    // their own type and class so one registry can advertise mixed asset types.
    TArray<FAdditionalRegistryRow> AdditionalRows;
    if (FParse::Value(*Params, TEXT("AdditionalRows="), AdditionalRowsText))
    {
        AdditionalRowsText.TrimQuotesInline();
        AdditionalRowsText.TrimStartAndEndInline();
        TArray<FString> RowTexts;
        AdditionalRowsText.ParseIntoArray(RowTexts, TEXT(";"), true);
        for (FString RowText : RowTexts)
        {
            TArray<FString> Fields;
            RowText.ParseIntoArray(Fields, TEXT("|"), false);
            if (Fields.Num() != 2 && Fields.Num() != 4)
            {
                UE_LOG(LogBatcomputerRegistryWriter, Error,
                    TEXT("Invalid AdditionalRows entry (expected Package|PrimaryAssetName or Package|PrimaryAssetName|PrimaryAssetType|Class): %s"),
                    *RowText);
                return 10;
            }
            for (FString& Field : Fields)
            {
                Field.TrimStartAndEndInline();
            }

            FAdditionalRegistryRow Row;
            Row.PackageName = Fields[0];
            Row.PrimaryAssetName = Fields[1];
            Row.PrimaryAssetType = Fields.Num() == 4 ? Fields[2] : PrimaryAssetType;
            Row.ClassPathText = Fields.Num() == 4 ? Fields[3] : ClassPathText;
            FString RowClassPackage;
            FString RowClassAsset;
            if (!FPackageName::IsValidLongPackageName(Row.PackageName) ||
                Row.PrimaryAssetName.IsEmpty() ||
                Row.PrimaryAssetType.IsEmpty() ||
                !Row.ClassPathText.Split(TEXT("."), &RowClassPackage, &RowClassAsset, ESearchCase::CaseSensitive, ESearchDir::FromEnd) ||
                RowClassPackage.IsEmpty() || RowClassAsset.IsEmpty())
            {
                UE_LOG(LogBatcomputerRegistryWriter, Error,
                    TEXT("Invalid AdditionalRows entry: %s"), *RowText);
                return 10;
            }
            Row.AssetClassPath = FTopLevelAssetPath(FName(*RowClassPackage), FName(*RowClassAsset));
            AdditionalRows.Add(MoveTemp(Row));
        }
    }

    // Character metadata preloads EquipmentList and UpgradeDataAssets through
    // METADATA, and actor classes through GAMEPLAY/CINEMATIC. ETAs then preload
    // their equipment definition through GAMEPLAY. Registering IDs alone is not enough.
    TMap<FString, FAssetBundleData> AssetBundles;
    FString AssetBundlesText, AssetBundlesFile;
    if (FParse::Value(*Params, TEXT("AssetBundlesFile="), AssetBundlesFile))
    {
        AssetBundlesFile.TrimQuotesInline();
        if (!FFileHelper::LoadFileToString(AssetBundlesText, *AssetBundlesFile))
        {
            UE_LOG(LogBatcomputerRegistryWriter, Error, TEXT("Could not read bundle input: %s"), *AssetBundlesFile);
            return 11;
        }
    }
    else if (FParse::Value(*Params, TEXT("AssetBundles="), AssetBundlesText))
    {
        AssetBundlesText.TrimQuotesInline();
    }
    FString LegacyBundlesText;
    if (FParse::Value(*Params, TEXT("GameplayBundles="), LegacyBundlesText))
    {
        // Keep standalone equipment proofs compatible; current Batcomputer uses
        // the named-bundle file protocol, avoiding command-line length limits.
        LegacyBundlesText.TrimQuotesInline();
        TArray<FString> LegacyRows;
        LegacyBundlesText.ParseIntoArray(LegacyRows, TEXT(";"), false);
        for (const FString& LegacyRow : LegacyRows)
        {
            FString LegacyPackage, LegacyAssets;
            if (!LegacyRow.Split(TEXT("|"), &LegacyPackage, &LegacyAssets)) return 11;
            if (!AssetBundlesText.IsEmpty()) AssetBundlesText += TEXT(";");
            AssetBundlesText += LegacyPackage + TEXT("|ASSETBUNDLE_GAMEPLAY|") + LegacyAssets;
        }
    }
    int32 ExpectedBundles = 0, ExpectedBundleAssets = 0;
    if (!AssetBundlesText.IsEmpty())
    {
        TArray<FString> BundleRows;
        AssetBundlesText.ParseIntoArray(BundleRows, TEXT(";"), false);
        for (const FString& BundleRow : BundleRows)
        {
            TArray<FString> Fields;
            BundleRow.ParseIntoArray(Fields, TEXT("|"), false);
            if (Fields.Num() != 3) return 11;
            const FString& BundlePackage = Fields[0];
            const FString& BundleName = Fields[1];
            const FString& AssetPathsText = Fields[2];
            if ((BundleName != TEXT("ASSETBUNDLE_GAMEPLAY") &&
                 BundleName != TEXT("ASSETBUNDLE_METADATA") &&
                 BundleName != TEXT("ASSETBUNDLE_CINEMATIC")) ||
                (AssetBundles.Contains(BundlePackage) && AssetBundles[BundlePackage].FindEntry(FName(*BundleName))) ||
                (BundlePackage != PackageName && !AdditionalRows.ContainsByPredicate(
                    [&](const FAdditionalRegistryRow& Row) { return Row.PackageName == BundlePackage; })))
            {
                UE_LOG(LogBatcomputerRegistryWriter, Error, TEXT("Invalid or duplicate bundle row: %s"), *BundleRow);
                return 11;
            }
            TArray<FString> AssetPaths;
            AssetPathsText.ParseIntoArray(AssetPaths, TEXT(","), false);
            FAssetBundleData& Bundle = AssetBundles.FindOrAdd(BundlePackage);
            TSet<FString> SeenPaths;
            for (const FString& Path : AssetPaths)
            {
                const FSoftObjectPath ObjectPathValue(Path);
                if (!ObjectPathValue.IsValid() || !ObjectPathValue.GetSubPathString().IsEmpty() ||
                    !FPackageName::IsValidObjectPath(Path) ||
                    Path.StartsWith(TEXT("/Script/"), ESearchCase::IgnoreCase) ||
                    Path.Contains(TEXT("|")) || Path.Contains(TEXT(";")) ||
                    Path.Contains(TEXT("\\")) || Path.Contains(TEXT("..")) ||
                    SeenPaths.Contains(Path.ToLower()))
                {
                    UE_LOG(LogBatcomputerRegistryWriter, Error, TEXT("Invalid bundle object path: %s"), *Path);
                    return 11;
                }
                SeenPaths.Add(Path.ToLower());
                Bundle.AddBundleAsset(FName(*BundleName), ObjectPathValue.GetAssetPath());
            }
            if (SeenPaths.IsEmpty()) return 11;
            ++ExpectedBundles;
            ExpectedBundleAssets += SeenPaths.Num();
        }
    }
    const TArray<int32> ChunkIds;

    FAssetRegistryState State;
    const auto AddPrimaryAssetRow =
        [&](const FString& InPackageName,
            const FString& InPrimaryAssetType,
            const FString& InPrimaryAssetName,
            const FTopLevelAssetPath& InAssetClassPath)
        {
            const FString InPackagePath =
                FPackageName::GetLongPackagePath(InPackageName);
            const FString InAssetName =
                FPackageName::GetShortName(InPackageName);

            FAssetDataTagMap Tags;
            Tags.Add(
                FPrimaryAssetId::PrimaryAssetTypeTag,
                InPrimaryAssetType);
            Tags.Add(
                FPrimaryAssetId::PrimaryAssetNameTag,
                InPrimaryAssetName);

            FAssetData* AssetData = new FAssetData(
                    FName(*InPackageName),
                    FName(*InPackagePath),
                    FName(*InAssetName),
                    InAssetClassPath,
                    MoveTemp(Tags),
                    ChunkIds,
                    0);
            if (const FAssetBundleData* Bundle = AssetBundles.Find(InPackageName))
            {
                AssetData->TaggedAssetBundles = MakeShared<FAssetBundleData, ESPMode::ThreadSafe>(*Bundle);
            }
            State.AddAssetData(AssetData);
        };

    AddPrimaryAssetRow(PackageName, PrimaryAssetType, PrimaryAssetName, AssetClassPath);
    for (const auto& AdditionalRow : AdditionalRows)
    {
        AddPrimaryAssetRow(
            AdditionalRow.PackageName,
            AdditionalRow.PrimaryAssetType,
            AdditionalRow.PrimaryAssetName,
            AdditionalRow.AssetClassPath);
    }

    // Optional, deliberately unbacked control row for the loose-plugin proof.
    // It uses the same class and the same two tag keys as the real row, but a
    // primary type the game does not scan or load. If this row keeps its tags
    // while the real row loses them, a later physical-package discovery is
    // replacing the real row rather than plugin deserialization filtering it.
    const FString SentinelPrimaryAssetType = TEXT("BatcomputerRegistrySentinel");
    const FString SentinelPrimaryAssetName =
        bWriteSentinel
            ? FPackageName::GetShortName(SentinelPackageName)
            : FString{};
    if (bWriteSentinel)
    {
        AddPrimaryAssetRow(
            SentinelPackageName,
            SentinelPrimaryAssetType,
            SentinelPrimaryAssetName,
            AssetClassPath);
    }

    // Plugin registries do not need dependency or package-data tables for this
    // top-level PrimaryDataAsset row. Keeping the state minimal also makes the
    // proof composable: a future writer can add one row per suit.
    FAssetRegistrySerializationOptions Options;
    Options.bSerializeAssetRegistry = true;
    Options.bSerializeDependencies = false;
    Options.bSerializeSearchableNameDependencies = false;
    Options.bSerializeManageDependencies = false;
    Options.bSerializePackageData = false;
    Options.DisableFilters();
    Options.CookTagsAsName.Add(FPrimaryAssetId::PrimaryAssetTypeTag);
    Options.CookTagsAsName.Add(FPrimaryAssetId::PrimaryAssetNameTag);

    // Match Unreal's cooker path in AssetRegistryGenerator.cpp. Runtime
    // AssetRegistry.bin files are written through an FArrayWriter with
    // editor-only data filtering enabled. A default FBufferArchive leaves
    // bFilterEditorOnlyData=false in the registry header; the stock editor
    // can round-trip that file, but the shipping game cannot safely append it
    // as a cooked plugin registry during startup.
    FArrayWriter Serialized;
    Serialized.SetFilterEditorOnly(true);
    if (!State.Save(Serialized, Options))
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("FAssetRegistryState::Save failed"));
        return 5;
    }

    // FAssetRegistryHeader is FGuid (16 bytes), version int32 (4 bytes),
    // then the serialized bFilterEditorOnlyData flag. Keep this assertion
    // close to the writer so a future refactor cannot silently regenerate
    // the editor-style header that the shipping startup path rejected.
    constexpr int32 FilterEditorOnlyHeaderOffset = 20;
    const bool bHasCookedHeader =
        Serialized.Num() > FilterEditorOnlyHeaderOffset &&
        Serialized[FilterEditorOnlyHeaderOffset] != 0;
    if (!bHasCookedHeader)
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("Generated registry is missing the cooked bFilterEditorOnlyData header flag"));
        return 9;
    }

    OutputPath = FPaths::ConvertRelativePathToFull(OutputPath);
    IFileManager::Get().MakeDirectory(
        *FPaths::GetPath(OutputPath),
        true);
    if (!FFileHelper::SaveArrayToFile(Serialized, *OutputPath))
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("Could not write %s"),
            *OutputPath);
        return 6;
    }

    FAssetRegistryState VerificationState;
    if (!FAssetRegistryState::LoadFromDisk(
            *OutputPath,
            FAssetRegistryLoadOptions(),
            VerificationState))
    {
        UE_LOG(
            LogBatcomputerRegistryWriter,
            Error,
            TEXT("The newly written registry did not load: %s"),
            *OutputPath);
        return 7;
    }

    int32 AssetCount = 0;
    int32 ExactBundleRows = 0, ExactBundles = 0, ExactBundleAssets = 0;
    bool bFoundExactRow = false;
    bool bFoundExactPrimaryId = false;
    TArray<FString> ExpectedObjectPaths;
    TArray<FString> ExpectedPrimaryAssetNames;
    TArray<FString> ExpectedPrimaryAssetTypes;
    TArray<FTopLevelAssetPath> ExpectedAssetClassPaths;
    TArray<FString> ExpectedPrimaryAssetIds;
    ExpectedObjectPaths.Reserve(1 + AdditionalRows.Num());
    ExpectedPrimaryAssetNames.Reserve(1 + AdditionalRows.Num());
    ExpectedObjectPaths.Add(ObjectPath);
    ExpectedPrimaryAssetNames.Add(PrimaryAssetName);
    ExpectedPrimaryAssetTypes.Add(PrimaryAssetType);
    ExpectedAssetClassPaths.Add(AssetClassPath);
    ExpectedPrimaryAssetIds.Add(PrimaryAssetType + TEXT(":") + PrimaryAssetName);
    for (const auto& AdditionalRow : AdditionalRows)
    {
        const FString AdditionalAssetName = FPackageName::GetShortName(AdditionalRow.PackageName);
        ExpectedObjectPaths.Add(
            AdditionalRow.PackageName +
            TEXT(".") +
            AdditionalAssetName);
        ExpectedPrimaryAssetNames.Add(AdditionalRow.PrimaryAssetName);
        ExpectedPrimaryAssetTypes.Add(AdditionalRow.PrimaryAssetType);
        ExpectedAssetClassPaths.Add(AdditionalRow.AssetClassPath);
        ExpectedPrimaryAssetIds.Add(
            AdditionalRow.PrimaryAssetType + TEXT(":") + AdditionalRow.PrimaryAssetName);
    }
    TArray<uint8> FoundExpectedRows;
    TArray<uint8> FoundExpectedPrimaryIds;
    FoundExpectedRows.Init(0, ExpectedObjectPaths.Num());
    FoundExpectedPrimaryIds.Init(0, ExpectedObjectPaths.Num());
    bool bFoundExactSentinelRow = !bWriteSentinel;
    bool bFoundExactSentinelPrimaryId = !bWriteSentinel;
    const FString SentinelObjectPath =
        bWriteSentinel
            ? SentinelPackageName + TEXT(".") + SentinelPrimaryAssetName
            : FString{};
    VerificationState.EnumerateAllAssets(
        [&](const FAssetData& AssetData)
        {
            ++AssetCount;
            if (const FAssetBundleData* ExpectedBundle = AssetBundles.Find(AssetData.PackageName.ToString()))
            {
                if (AssetData.TaggedAssetBundles && BundlesMatch(*AssetData.TaggedAssetBundles, *ExpectedBundle))
                {
                    ++ExactBundleRows;
                    ExactBundles += ExpectedBundle->Bundles.Num();
                    for (const FAssetBundleEntry& Entry : ExpectedBundle->Bundles)
                    {
                        ExactBundleAssets += Entry.AssetPaths.Num();
                    }
                }
            }
            const FString AssetObjectPath =
                AssetData.GetObjectPathString();
            bool bMatchedExpectedPrimaryRow = false;
            for (int32 ExpectedIndex = 0;
                 ExpectedIndex < ExpectedObjectPaths.Num();
                 ++ExpectedIndex)
            {
                if (AssetObjectPath !=
                    ExpectedObjectPaths[ExpectedIndex])
                {
                    continue;
                }

                bMatchedExpectedPrimaryRow = true;
                FoundExpectedRows[ExpectedIndex] =
                    AssetData.AssetClassPath == ExpectedAssetClassPaths[ExpectedIndex]
                        ? 1
                        : 0;
                const FPrimaryAssetId Id =
                    AssetData.GetPrimaryAssetId();
                FoundExpectedPrimaryIds[ExpectedIndex] =
                    Id.PrimaryAssetType == FName(*ExpectedPrimaryAssetTypes[ExpectedIndex]) &&
                        Id.PrimaryAssetName ==
                            FName(
                                *ExpectedPrimaryAssetNames[
                                    ExpectedIndex])
                        ? 1
                        : 0;
                if (ExpectedIndex == 0)
                {
                    bFoundExactRow =
                        FoundExpectedRows[ExpectedIndex] != 0;
                    bFoundExactPrimaryId =
                        FoundExpectedPrimaryIds[ExpectedIndex] != 0;
                }
                break;
            }

            if (!bMatchedExpectedPrimaryRow &&
                bWriteSentinel &&
                AssetObjectPath == SentinelObjectPath)
            {
                bFoundExactSentinelRow =
                    AssetData.AssetClassPath == AssetClassPath;
                const FPrimaryAssetId Id = AssetData.GetPrimaryAssetId();
                bFoundExactSentinelPrimaryId =
                    Id.PrimaryAssetType == FName(*SentinelPrimaryAssetType) &&
                    Id.PrimaryAssetName == FName(*SentinelPrimaryAssetName);
            }
        });

    int32 ExactPrimaryRows = 0;
    int32 ExactPrimaryIds = 0;
    for (int32 ExpectedIndex = 0;
         ExpectedIndex < ExpectedObjectPaths.Num();
         ++ExpectedIndex)
    {
        ExactPrimaryRows +=
            FoundExpectedRows[ExpectedIndex] != 0 ? 1 : 0;
        ExactPrimaryIds +=
            FoundExpectedPrimaryIds[ExpectedIndex] != 0 ? 1 : 0;
    }
    const bool bFoundAllExpectedRows =
        ExactPrimaryRows == ExpectedObjectPaths.Num();
    const bool bFoundAllExpectedPrimaryIds =
        ExactPrimaryIds == ExpectedObjectPaths.Num();
    const FString ExpectedPrimaryAssetIdsText =
        FString::Join(ExpectedPrimaryAssetIds, TEXT("|"));
    const bool bFoundAllBundles = ExactBundleRows == AssetBundles.Num() &&
        ExactBundles == ExpectedBundles && ExactBundleAssets == ExpectedBundleAssets;

    UE_LOG(
        LogBatcomputerRegistryWriter,
        Display,
        TEXT("BATCOMPUTER_REGISTRY_WRITER_RESULT output=%s bytes=%lld cooked_header=%s assets=%d expected_primary_rows=%d exact_primary_rows=%d exact_primary_ids=%d expected_primary_asset_ids=%s package=%s object=%s class=%s primary_id=%s:%s exact_row=%s exact_primary_id=%s additional_rows=%d all_expected_rows=%s all_expected_primary_ids=%s sentinel_enabled=%s sentinel_object=%s sentinel_primary_id=%s:%s sentinel_exact_row=%s sentinel_exact_primary_id=%s exact_bundle_rows=%d exact_bundles=%d exact_bundle_assets=%d all_expected_bundles=%s"),
        *OutputPath,
        static_cast<long long>(Serialized.Num()),
        bHasCookedHeader ? TEXT("yes") : TEXT("no"),
        AssetCount,
        ExpectedObjectPaths.Num(),
        ExactPrimaryRows,
        ExactPrimaryIds,
        *ExpectedPrimaryAssetIdsText,
        *PackageName,
        *ObjectPath,
        *ClassPathText,
        *PrimaryAssetType,
        *PrimaryAssetName,
        bFoundExactRow ? TEXT("yes") : TEXT("no"),
        bFoundExactPrimaryId ? TEXT("yes") : TEXT("no"),
        AdditionalRows.Num(),
        bFoundAllExpectedRows ? TEXT("yes") : TEXT("no"),
        bFoundAllExpectedPrimaryIds ? TEXT("yes") : TEXT("no"),
        bWriteSentinel ? TEXT("yes") : TEXT("no"),
        bWriteSentinel ? *SentinelObjectPath : TEXT("<none>"),
        bWriteSentinel ? *SentinelPrimaryAssetType : TEXT("<none>"),
        bWriteSentinel ? *SentinelPrimaryAssetName : TEXT("<none>"),
        bFoundExactSentinelRow ? TEXT("yes") : TEXT("no"),
        bFoundExactSentinelPrimaryId ? TEXT("yes") : TEXT("no"),
        ExactBundleRows,
        ExactBundles,
        ExactBundleAssets,
        bFoundAllBundles ? TEXT("yes") : TEXT("no"));

    return bFoundAllExpectedRows &&
                   bFoundAllExpectedPrimaryIds &&
                   bFoundAllBundles &&
                   bFoundExactSentinelRow &&
                   bFoundExactSentinelPrimaryId
               ? 0
               : 8;
}
