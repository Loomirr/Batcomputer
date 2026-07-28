using UnrealBuildTool;

public class SuitSlotsRegistryWriter : ModuleRules
{
    public SuitSlotsRegistryWriter(ReadOnlyTargetRules Target) : base(Target)
    {
        PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

        PublicDependencyModuleNames.AddRange(new[]
        {
            "Core",
            "CoreUObject",
            "Engine",
            "AssetRegistry"
        });
    }
}
