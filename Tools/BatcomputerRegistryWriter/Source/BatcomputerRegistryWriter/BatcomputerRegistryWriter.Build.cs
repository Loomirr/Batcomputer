using UnrealBuildTool;

public class BatcomputerRegistryWriter : ModuleRules
{
    public BatcomputerRegistryWriter(ReadOnlyTargetRules Target) : base(Target)
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
