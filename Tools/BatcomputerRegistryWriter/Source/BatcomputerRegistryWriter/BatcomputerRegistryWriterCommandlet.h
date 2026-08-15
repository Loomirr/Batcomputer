#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "BatcomputerRegistryWriterCommandlet.generated.h"

UCLASS()
class BATCOMPUTERREGISTRYWRITER_API UBatcomputerRegistryWriterCommandlet final : public UCommandlet
{
    GENERATED_BODY()

public:
    UBatcomputerRegistryWriterCommandlet();

    virtual int32 Main(const FString& Params) override;
};
