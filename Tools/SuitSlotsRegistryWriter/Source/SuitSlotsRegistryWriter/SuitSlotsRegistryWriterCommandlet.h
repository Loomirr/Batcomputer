#pragma once

#include "CoreMinimal.h"
#include "Commandlets/Commandlet.h"
#include "SuitSlotsRegistryWriterCommandlet.generated.h"

UCLASS()
class SUITSLOTSREGISTRYWRITER_API USuitSlotsRegistryWriterCommandlet final : public UCommandlet
{
    GENERATED_BODY()

public:
    USuitSlotsRegistryWriterCommandlet();

    virtual int32 Main(const FString& Params) override;
};
