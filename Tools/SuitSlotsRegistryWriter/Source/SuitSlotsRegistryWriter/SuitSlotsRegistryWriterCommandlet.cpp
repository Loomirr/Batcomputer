#include "SuitSlotsRegistryWriterCommandlet.h"

#include "AssetRegistry/AssetData.h"
#include "AssetRegistry/AssetRegistryState.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Serialization/ArrayWriter.h"
#include "UObject/PrimaryAssetId.h"

DEFINE_LOG_CATEGORY_STATIC(LogSuitSlotsRegistryWriter, Log, All);

namespace
{
bool ReadRequiredValue(
    const FString& Params,
    const TCHAR* Key,
    FString& OutValue)
{
    if (!FParse::Value(*Params, Key, OutValue))
    {
        UE_LOG(
            LogSuitSlotsRegistryWriter,
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
            LogSuitSlotsRegistryWriter,
            Error,
            TEXT("Argument %s must not be empty"),
            Key);
        return false;
    }
    return true;
}
}

USuitSlotsRegistryWriterCommandlet::USuitSlotsRegistryWriterCommandlet()
{
    IsClient = false;
    IsEditor = true;
    IsServer = false;
    LogToConsole = true;
    ShowErrorCount = true;
}

int32 USuitSlotsRegistryWriterCommandlet::Main(const FString& Params)
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
                LogSuitSlotsRegistryWriter,
                Error,
                TEXT("Invalid SentinelPackage long package name: %s"),
                *SentinelPackageName);
            return 3;
        }
    }

    if (!FPackageName::IsValidLongPackageName(PackageName))
    {
        UE_LOG(
            LogSuitSlotsRegistryWriter,
            Error,
            TEXT("Invalid long package name: %s"),
            *PackageName);
        return 3;
    }

    // Optional multi-suit rows use:
    //   -AdditionalRows="/Game/Path/Asset|PrimaryAssetName;/Game/Other/Asset|OtherName"
    // The class and PrimaryAssetType are shared with the required first row.
    // This keeps the original one-row command line compatible while allowing a
    // packaged suit mod to contribute every DCMD before runtime systems cache
    // PawnMetaData.
    TArray<TPair<FString, FString>> AdditionalRows;
    if (FParse::Value(
            *Params,
            TEXT("AdditionalRows="),
            AdditionalRowsText))
    {
        AdditionalRowsText.TrimQuotesInline();
        AdditionalRowsText.TrimStartAndEndInline();

        TArray<FString> RowTexts;
        AdditionalRowsText.ParseIntoArray(
            RowTexts,
            TEXT(";"),
            true);
        for (FString RowText : RowTexts)
        {
            RowText.TrimStartAndEndInline();
            FString AdditionalPackage;
            FString AdditionalPrimaryAssetName;
            if (!RowText.Split(
                    TEXT("|"),
                    &AdditionalPackage,
                    &AdditionalPrimaryAssetName,
                    ESearchCase::CaseSensitive,
                    ESearchDir::FromStart))
            {
                UE_LOG(
                    LogSuitSlotsRegistryWriter,
                    Error,
                    TEXT("Invalid AdditionalRows entry (expected Package|PrimaryAssetName): %s"),
                    *RowText);
                return 10;
            }

            AdditionalPackage.TrimStartAndEndInline();
            AdditionalPrimaryAssetName.TrimStartAndEndInline();
            if (!FPackageName::IsValidLongPackageName(
                    AdditionalPackage) ||
                AdditionalPrimaryAssetName.IsEmpty())
            {
                UE_LOG(
                    LogSuitSlotsRegistryWriter,
                    Error,
                    TEXT("Invalid AdditionalRows entry package=%s primary_asset_name=%s"),
                    *AdditionalPackage,
                    *AdditionalPrimaryAssetName);
                return 10;
            }

            AdditionalRows.Emplace(
                MoveTemp(AdditionalPackage),
                MoveTemp(AdditionalPrimaryAssetName));
        }
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
            LogSuitSlotsRegistryWriter,
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

    const TArray<int32> ChunkIds;

    FAssetRegistryState State;
    const auto AddPrimaryAssetRow =
        [&](const FString& InPackageName,
            const FString& InPrimaryAssetType,
            const FString& InPrimaryAssetName)
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

            State.AddAssetData(
                new FAssetData(
                    FName(*InPackageName),
                    FName(*InPackagePath),
                    FName(*InAssetName),
                    AssetClassPath,
                    MoveTemp(Tags),
                    ChunkIds,
                    0));
        };

    AddPrimaryAssetRow(PackageName, PrimaryAssetType, PrimaryAssetName);
    for (const auto& AdditionalRow : AdditionalRows)
    {
        AddPrimaryAssetRow(
            AdditionalRow.Key,
            PrimaryAssetType,
            AdditionalRow.Value);
    }

    // Optional, deliberately unbacked control row for the loose-plugin proof.
    // It uses the same class and the same two tag keys as the real row, but a
    // primary type the game does not scan or load. If this row keeps its tags
    // while the real row loses them, a later physical-package discovery is
    // replacing the real row rather than plugin deserialization filtering it.
    const FString SentinelPrimaryAssetType = TEXT("SuitSlotsRegistrySentinel");
    const FString SentinelPrimaryAssetName =
        bWriteSentinel
            ? FPackageName::GetShortName(SentinelPackageName)
            : FString{};
    if (bWriteSentinel)
    {
        AddPrimaryAssetRow(
            SentinelPackageName,
            SentinelPrimaryAssetType,
            SentinelPrimaryAssetName);
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
            LogSuitSlotsRegistryWriter,
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
            LogSuitSlotsRegistryWriter,
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
            LogSuitSlotsRegistryWriter,
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
            LogSuitSlotsRegistryWriter,
            Error,
            TEXT("The newly written registry did not load: %s"),
            *OutputPath);
        return 7;
    }

    int32 AssetCount = 0;
    bool bFoundExactRow = false;
    bool bFoundExactPrimaryId = false;
    TArray<FString> ExpectedObjectPaths;
    TArray<FString> ExpectedPrimaryAssetNames;
    ExpectedObjectPaths.Reserve(1 + AdditionalRows.Num());
    ExpectedPrimaryAssetNames.Reserve(1 + AdditionalRows.Num());
    ExpectedObjectPaths.Add(ObjectPath);
    ExpectedPrimaryAssetNames.Add(PrimaryAssetName);
    for (const auto& AdditionalRow : AdditionalRows)
    {
        const FString AdditionalAssetName =
            FPackageName::GetShortName(AdditionalRow.Key);
        ExpectedObjectPaths.Add(
            AdditionalRow.Key +
            TEXT(".") +
            AdditionalAssetName);
        ExpectedPrimaryAssetNames.Add(AdditionalRow.Value);
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
                    AssetData.AssetClassPath == AssetClassPath
                        ? 1
                        : 0;
                const FPrimaryAssetId Id =
                    AssetData.GetPrimaryAssetId();
                FoundExpectedPrimaryIds[ExpectedIndex] =
                    Id.PrimaryAssetType ==
                            FName(*PrimaryAssetType) &&
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

    UE_LOG(
        LogSuitSlotsRegistryWriter,
        Display,
        TEXT("SUIT_SLOTS_REGISTRY_WRITER_RESULT output=%s bytes=%lld cooked_header=%s assets=%d expected_primary_rows=%d exact_primary_rows=%d exact_primary_ids=%d package=%s object=%s class=%s primary_id=%s:%s exact_row=%s exact_primary_id=%s additional_rows=%d all_expected_rows=%s all_expected_primary_ids=%s sentinel_enabled=%s sentinel_object=%s sentinel_primary_id=%s:%s sentinel_exact_row=%s sentinel_exact_primary_id=%s"),
        *OutputPath,
        static_cast<long long>(Serialized.Num()),
        bHasCookedHeader ? TEXT("yes") : TEXT("no"),
        AssetCount,
        ExpectedObjectPaths.Num(),
        ExactPrimaryRows,
        ExactPrimaryIds,
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
        bFoundExactSentinelPrimaryId ? TEXT("yes") : TEXT("no"));

    return bFoundAllExpectedRows &&
                   bFoundAllExpectedPrimaryIds &&
                   bFoundExactSentinelRow &&
                   bFoundExactSentinelPrimaryId
               ? 0
               : 8;
}
