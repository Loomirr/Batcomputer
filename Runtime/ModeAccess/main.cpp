// Experimental, fail-closed bridge for the native character roster in build 1344350.
// No save/progression writes, asset injection, entitlement changes or global tag-container edits.
#include <Mod/CppUserModBase.hpp>
#include <DynamicOutput/DynamicOutput.hpp>
#include <UE4SSProgram.hpp>
#include <LuaMadeSimple/LuaMadeSimple.hpp>
#include <Unreal/UObject.hpp>
#include <Unreal/UObjectGlobals.hpp>
#include <Unreal/Hooks.hpp>
#include <Unreal/FString.hpp>
#include <Unreal/CoreUObject/UObject/Class.hpp>
#include <Unreal/CoreUObject/UObject/UnrealType.hpp>
#include <Windows.h>
#include <intrin.h>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <vector>
#include <array>
#include <mutex>
#include "Policy.hpp"
#include "CallSite.hpp"

using namespace RC;
using namespace RC::Unreal;
namespace {
ModeAccess::PolicyMap policies;
std::atomic_bool villainsInNormal{}, heroesInMayhem{}, ready{};
std::atomic_bool villainsDebug{}, heroesDebug{};
uintptr_t image{};
ModeAccess::CallSite matchCall, availableCall, toggleCall, previewLockCall, partyPinCall;
Hook::GlobalCallbackId previewCallback{Hook::ERROR_ID}, suitLockCallback{Hook::ERROR_ID};
constexpr uintptr_t RosterRva=0x7270260, MatchRva=0x23405F8, AvailableRva=0x638DBF8;
constexpr uintptr_t MatchReturnRva=0x727038F, AvailableReturnRva=0x72703D2;
constexpr uintptr_t CompleteRva=0x638F3EC, CompleteReturnRva=0x727034D;
thread_local ModeAccess::PendingAvailability pendingAvailability;
std::mutex rosterModesMutex;
std::map<uintptr_t,bool> rosterModes;
constexpr uintptr_t TagMatchesRva=0x2340764, ToggleReturnRva=0x726E75A;
constexpr uintptr_t PreviewLockedRva=0x770BF48, PreviewLockedReturnRva=0x772097A;
constexpr uintptr_t PartyPinReturnRva=0x768BD96, ProgressEntryRva=0x24DABD4;

void log(const std::wstring& text) { Output::send<LogLevel::Default>(STR("[LOTDKModeAccess] {}\n"),text); }
void trace(const std::wstring& text) { if(villainsDebug || heroesDebug) log(text); }

UObject* objectProperty(UObject* object,const wchar_t* name) {
    auto* property=object?CastField<FObjectPropertyBase>(object->GetPropertyByNameInChain(name)):nullptr;
    return property?property->GetObjectPropertyValue(property->ContainerPtrToValuePtr<void>(object)):nullptr;
}
std::wstring tagResult(UObject* object,const wchar_t* name) {
    auto* getter=object?object->GetFunctionByNameInChain(name):nullptr;
    auto* result=getter?CastField<FStructProperty>(getter->GetReturnProperty()):nullptr;
    if(!result || !result->GetStruct() || result->GetStruct()->GetName()!=STR("GameplayTag")) return {};
    const int size=getter->GetStructureSize();
    if(size<=0 || size>256 || result->GetOffset_Internal()<0 || result->GetOffset_Internal()+result->GetSize()>size) return {};
    std::vector<uint8_t> params(size,0);getter->InitializeStruct(params.data());
    struct Destroy {UFunction* fn;void* data;~Destroy(){fn->DestroyStruct(data);}} destroy{getter,params.data()};
    object->ProcessEvent(getter,params.data());
    FString exported;auto* value=result->ContainerPtrToValuePtr<void>(params.data());
    result->ExportTextItem(exported,value,value,object,0);
    const std::wstring text=*exported,prefix=L"(TagName=\"";
    if(!text.starts_with(prefix) || !text.ends_with(L"\")")) return {};
    return text.substr(prefix.size(),text.size()-prefix.size()-2);
}
bool activated(UObject* object) {
    auto* fn=object?object->GetFunctionByNameInChain(STR("IsActivated")):nullptr;
    auto* result=fn?CastField<FBoolProperty>(fn->GetReturnProperty()):nullptr;
    if(!result || fn->GetStructureSize()<=0 || fn->GetStructureSize()>64) return false;
    std::vector<uint8_t> params(fn->GetStructureSize(),0);
    fn->InitializeStruct(params.data());object->ProcessEvent(fn,params.data());
    const bool value=result->GetPropertyValue(result->ContainerPtrToValuePtr<void>(params.data()));
    fn->DestroyStruct(params.data());return value;
}
bool __fastcall previewLocked(UObject* screen,UObject* metadata) {
    const auto caller=reinterpret_cast<uintptr_t>(_ReturnAddress());
    const bool native=reinterpret_cast<bool(__fastcall*)(UObject*,UObject*)>(image+PreviewLockedRva)(screen,metadata);
    // This ONE character-preview dispatch call feeds both the async pocket actor's
    // locked-material flag and SetPreviewLocked. Do not change the shared query.
    if(!ready || !native || caller!=image+PreviewLockedReturnRva || !screen || !metadata ||
       (!villainsInNormal && !heroesInMayhem)) return native;
    try {
        if(!screen->GetClassPrivate() || screen->GetClassPrivate()->GetPathName()!=
           STR("/Game/UI/GameMenu/W_CharacterScreen.W_CharacterScreen_C")) return native;
        auto* mode=CastField<FBoolProperty>(screen->GetPropertyByNameInChain(STR("IsVM")));
        auto* tagProperty=CastField<FStructProperty>(metadata->GetPropertyByNameInChain(STR("PawnTag")));
        if(!mode || !tagProperty || !tagProperty->GetStruct() ||
           tagProperty->GetStruct()->GetName()!=STR("GameplayTag") ||
           tagProperty->GetOffset_Internal()!=0x38 || tagProperty->GetSize()!=sizeof(FName)) return native;
        const auto tag=tagProperty->ContainerPtrToValuePtr<FName>(metadata)->ToString();
        const bool mayhem=mode->GetPropertyValue(mode->ContainerPtrToValuePtr<void>(screen));
        if(ModeAccess::preview_locked_result(native,true,tag,mayhem,policies,villainsInNormal,heroesInMayhem)!=native) {
            static unsigned reports{};
            if(reports++<40)trace(L"Native preview material unlocked: "+tag+std::wstring(mayhem?L" [Mayhem]":L" [Normal]"));
            return false;
        }
    }catch(const std::exception&){}
    return native;
}
void unlockSuitButton(UObject* button,UFunction* function,void* args) {
    static thread_local bool internal=false;
    if(!ready || internal || !button || !function || !args || function->GetName()!=STR("BP_OnLockedChanged") ||
       (!villainsInNormal && !heroesInMayhem)) return;
    if(!button->GetClassPrivate() || button->GetClassPrivate()->GetPathName()!=
       STR("/Game/UI/GameMenu/CharacterSelect/W_CharacterSubMenuButton.W_CharacterSubMenuButton_C")) return;
    auto* changed=CastField<FBoolProperty>(function->GetPropertyByNameInChain(STR("bIsLocked")));
    if(!changed || !changed->GetPropertyValue(changed->ContainerPtrToValuePtr<void>(args))) return;
    UObject* screen=button;
    for(int depth=0;screen && depth<16;++depth) {
        if(screen->GetClassPrivate() && screen->GetClassPrivate()->GetPathName()==
           STR("/Game/UI/GameMenu/W_CharacterScreen.W_CharacterScreen_C")) break;
        screen=screen->GetOuterPrivate();
    }
    if(!screen || !screen->GetClassPrivate() || screen->GetClassPrivate()->GetPathName()!=
       STR("/Game/UI/GameMenu/W_CharacterScreen.W_CharacterScreen_C")) return;
    auto* mode=CastField<FBoolProperty>(screen->GetPropertyByNameInChain(STR("IsVM")));
    if(!mode) return;
    struct Guard {bool& value;Guard(bool& v):value(v){value=true;}~Guard(){value=false;}} guard(internal);
    const auto tag=tagResult(button,STR("GetTag"));
    const bool mayhem=mode->GetPropertyValue(mode->ContainerPtrToValuePtr<void>(screen));
    if(!ModeAccess::allow_cross_mode_preview(tag,mayhem,policies,villainsInNormal,heroesInMayhem)) return;
    auto* setter=button->GetFunctionByNameInChain(STR("SetIsLocked"));
    if(!setter || setter->GetNumParms()!=1 || setter->GetReturnProperty() ||
       setter->GetStructureSize()<=0 || setter->GetStructureSize()>64) return;
    FBoolProperty* parameter=nullptr;
    for(auto* prop:TFieldRange<FProperty>(setter,EFieldIterationFlags::IncludeDeprecated)) {
        if(prop && prop->HasAnyPropertyFlags(CPF_Parm)) parameter=CastField<FBoolProperty>(prop);
    }
    if(!parameter) return;
    std::vector<uint8_t> params(setter->GetStructureSize(),0);setter->InitializeStruct(params.data());
    parameter->SetPropertyValue(parameter->ContainerPtrToValuePtr<void>(params.data()),false);
    button->ProcessEvent(setter,params.data());setter->DestroyStruct(params.data());
    static unsigned reports{};if(reports++<20)trace(L"Cross-mode suit button unlocked (UI only): "+tag);
}

void preview(UObject* screen,UFunction* function,void* args) {
    // Only the real character screen's cosmetic lock event. No global unlock,
    // mission progress, button prohibition, entitlement or save flags are changed.
    static thread_local bool probing=false;
    if(!ready || probing || !screen || !function || !args ||
       (!villainsInNormal && !heroesInMayhem) || function->GetName()!=STR("SetPreviewLocked")) return;
    if(!screen->GetClassPrivate() || screen->GetClassPrivate()->GetPathName()!=
       STR("/Game/UI/GameMenu/W_CharacterScreen.W_CharacterScreen_C")) return;
    auto* locked=CastField<FBoolProperty>(function->GetPropertyByNameInChain(STR("bIsLocked")));
    auto* mode=CastField<FBoolProperty>(screen->GetPropertyByNameInChain(STR("IsVM")));
    if(!locked || !mode) return;
    if(!locked->GetPropertyValue(locked->ContainerPtrToValuePtr<void>(args))) return;
    struct Probe {bool& value;Probe(bool& v):value(v){value=true;}~Probe(){value=false;}} guard(probing);
    // GetSelectedOption belongs to the SUBMENU, not CharacterSelectScreen.
    // Never use an inactive suit menu's previous character to authorize a preview.
    auto* suits=objectProperty(screen,STR("SuitMenu"));
    auto* mainMenu=objectProperty(screen,STR("MainMenu"));
    const bool suitActive=activated(suits), mainActive=activated(mainMenu);
    const auto suitTag=suitActive?tagResult(suits,STR("GetSelectedOption")):std::wstring{};
    const auto mainTag=!suitActive && mainActive?tagResult(objectProperty(mainMenu,STR("CachedSelectedButton")),STR("GetTag")):std::wstring{};
    const auto tag=ModeAccess::preview_selection(suitActive,suitTag,mainActive,mainTag);
    const bool mayhem=mode->GetPropertyValue(mode->ContainerPtrToValuePtr<void>(screen));
    static unsigned probes{};
    if(probes++<20) trace(L"Preview lock probe: mode="+std::wstring(mayhem?L"Mayhem":L"Normal")+L" active-menu tag="+(tag.empty()?L"<unresolved>":tag));
    if(ModeAccess::allow_cross_mode_preview(tag,mayhem,policies,villainsInNormal,heroesInMayhem)) {
        locked->SetPropertyValue(locked->ContainerPtrToValuePtr<void>(args),false);
        static unsigned reports{};
        if(reports++<12) trace(L"Cross-mode preview unlocked (UI only): "+tag);
    }
}

void loadPolicies() {
    const auto root=std::filesystem::path(UE4SSProgram::get_program().get_working_directory())/L"LOTDKExpanded"/L"RegistryPlugins";
    std::error_code ec;
    if(!std::filesystem::is_directory(root,ec)) return;
    for(const auto& dir:std::filesystem::directory_iterator(root,ec)) {
        if(!dir.is_directory()) continue;
        if(!std::filesystem::exists(dir.path()/(dir.path().filename().wstring()+L".uplugin"))) continue;
        const auto file=dir.path()/L"Config"/L"BatcomputerCharacterModes.ini";
        if(!std::filesystem::exists(file)) continue;
        std::ifstream input(file); std::string line; bool section=false;
        ModeAccess::PolicyMap incoming;
        bool valid=true;
        while(std::getline(input,line)) {
            if(!line.empty() && line.back()=='\r') line.pop_back();
            if(line.empty() || line[0]==';' || line[0]=='#') continue;
            if(line=="[Batcomputer.CharacterModes.v1]") {section=true;continue;}
            const auto eq=line.find('=');
            if(!section || eq==std::string::npos) {valid=false;break;}
            std::wstring scope(line.begin(),line.begin()+eq);
            const auto text=line.substr(eq+1);
            auto mode=text=="normal"?ModeAccess::Mode::Normal:text=="mayhem"?ModeAccess::Mode::Mayhem:text=="both"?ModeAccess::Mode::Both:ModeAccess::Mode::Conflict;
            if(!ModeAccess::valid_scope(scope)||mode==ModeAccess::Mode::Conflict||incoming.contains(scope)) {valid=false;break;}
            incoming.emplace(scope,mode);
        }
        if(!valid || !section || input.bad()) {log(L"Ignoring invalid policy: "+file.wstring());continue;}
        for(const auto& [scope,mode]:incoming) {
            auto [it,inserted]=policies.emplace(scope,mode);
            if(!inserted && it->second!=mode) {it->second=ModeAccess::Mode::Conflict;log(L"Conflicting policies; retaining native behavior for "+scope);}
        }
    }
    trace(L"Loaded "+std::to_wstring(policies.size())+L" character policies.");
}

bool __fastcall matches(void* tag,void* container,bool mayhem) {
    auto caller=reinterpret_cast<uintptr_t>(_ReturnAddress());
    bool native=reinterpret_cast<bool(__fastcall*)(void*,void*)>(image+MatchRva)(tag,container);
    const auto callerSlot=reinterpret_cast<uintptr_t>(_AddressOfReturnAddress());
    pendingAvailability.set(callerSlot,false);
    if(!ready || caller!=image+MatchReturnRva) return native;
    {std::lock_guard lock(rosterModesMutex);rosterModes[reinterpret_cast<uintptr_t>(container)]=mayhem;}
    try {
        auto text=reinterpret_cast<FName*>(tag)->ToString();
        if(auto decision=ModeAccess::decide(text,mayhem,policies,villainsInNormal,heroesInMayhem)) {
            const bool crossModeUnlock=decision->include && decision->crossModeUnlock;
            pendingAvailability.set(callerSlot,crossModeUnlock);
            static unsigned reports{};
            if(crossModeUnlock && reports++<8) trace(std::wstring(mayhem?L"Mayhem":L"Normal")+L" roster crossover: "+text);
            return decision->include?mayhem:!mayhem;
        }
    } catch(const std::exception&) { /* Preserve the original result on a failed conversion. */ }
    return native;
}
bool __fastcall toggleLocked(void* pawnTag,void* configuredGroup) {
    const auto caller=reinterpret_cast<uintptr_t>(_ReturnAddress());
    const bool native=reinterpret_cast<bool(__fastcall*)(void*,void*)>(image+TagMatchesRva)(pawnTag,configuredGroup);
    if(!ready || !heroesInMayhem || !native || caller!=image+ToggleReturnRva) return native;
    // This exact party-toggle call compares against system.ToggleLockedCharacterGroup
    // at +0x90. Use only mode observations from that SAME system's roster (+0xA0).
    bool mayhem=false;
    {std::lock_guard lock(rosterModesMutex);auto found=rosterModes.find(reinterpret_cast<uintptr_t>(configuredGroup)+0x10);
        if(found==rosterModes.end()) return native;mayhem=found->second;}
    try {
        const auto pawn=reinterpret_cast<FName*>(pawnTag)->ToString();
        const auto group=reinterpret_cast<FName*>(configuredGroup)->ToString();
        if(ModeAccess::allow_batman_toggle(mayhem,heroesInMayhem,pawn,group)) {
            static unsigned reports{};if(reports++<6)trace(L"Mayhem party toggle: allowing Batman to leave the active slot.");
            return false;
        }
    }catch(const std::exception&){}
    return native;
}
bool __fastcall partyPinned(void* pawnTag,void* configuredGroup) {
    const auto caller=reinterpret_cast<uintptr_t>(_ReturnAddress());
    const bool native=reinterpret_cast<bool(__fastcall*)(void*,void*)>(image+TagMatchesRva)(pawnTag,configuredGroup);
    if(!ready || !heroesInMayhem || !native || caller!=image+PartyPinReturnRva || !pawnTag || !configuredGroup) return native;
    try {
        // FreePartner identified BatmanGroupTag on party preferences. Override only
        // the native pin predicate's comparison, never that stored preference or
        // the other code that uses it to remember/restore preferred identities.
        auto* prefs=reinterpret_cast<UObject*>(reinterpret_cast<uintptr_t>(configuredGroup)-0x1A0);
        if(!prefs->GetClassPrivate() || prefs->GetClassPrivate()->GetPathName()!=
           STR("/Game/Global/Party/Config/BP_DinnerPartyPlayerPrefs.BP_DinnerPartyPlayerPrefs_C") ||
           prefs->GetName().starts_with(STR("Default__"))) return native;
        auto* property=CastField<FStructProperty>(prefs->GetPropertyByNameInChain(STR("BatmanGroupTag")));
        if(!property || !property->GetStruct() || property->GetStruct()->GetName()!=STR("GameplayTag") ||
           property->GetOffset_Internal()!=0x1A0 || property->GetSize()!=sizeof(FName) ||
           property->ContainerPtrToValuePtr<void>(prefs)!=configuredGroup) return native;
        const auto pawn=reinterpret_cast<FName*>(pawnTag)->ToString();
        const auto group=reinterpret_cast<FName*>(configuredGroup)->ToString();
        if(!ModeAccess::allow_batman_toggle(true,true,pawn,group)) return native;
        // Fresh, read-only query through this party's registered save-system world
        // context. Never reuse the last menu's mode across a normal/Mayhem change.
        auto* context=objectProperty(prefs,STR("RegisteredSaveSystem"));
        FName modeTag{STR("GameProgress.Definitions.DLC.VillainModeSave"),FNAME_Find};
        void* entry=context && modeTag.ToString()==STR("GameProgress.Definitions.DLC.VillainModeSave") ?
            reinterpret_cast<void*(__fastcall*)(UObject*,FName*)>(image+ProgressEntryRva)(context,&modeTag):nullptr;
        const bool mayhem=entry && reinterpret_cast<bool(__fastcall*)(void*)>(image+CompleteRva)(entry);
        if(ModeAccess::party_pin_result(native,entry!=nullptr,mayhem,heroesInMayhem,pawn,group)!=native) {
            static unsigned reports{};if(reports++<12)trace(L"Mayhem party preference pin bypassed: "+pawn);
            return false;
        }
        if(!entry) {static unsigned reports{};if(reports++<4)log(L"Party pin unchanged: current mode could not be resolved from party save context.");}
    }catch(const std::exception&){}
    return native;
}
bool __fastcall available(void* entry) {
    auto caller=reinterpret_cast<uintptr_t>(_ReturnAddress());
    const bool pending=pendingAvailability.take(reinterpret_cast<uintptr_t>(_AddressOfReturnAddress()));
    const bool unlock=ready && pending && caller==image+AvailableReturnRva;
    bool native=reinterpret_cast<bool(__fastcall*)(void*)>(image+AvailableRva)(entry);
    // Only the roster's availability query for a requested cross-mode native character.
    // Never alter GameProgress values, completion checks, missions or DLC ownership.
    return native || unlock;
}

template<size_t N> bool bytes(uintptr_t rva,const std::array<unsigned char,N>& expected) {
    return std::memcmp(reinterpret_cast<void*>(image+rva),expected.data(),N)==0;
}
bool supported() {
    image=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    auto* dos=reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE) return false;
    auto* pe=reinterpret_cast<IMAGE_NT_HEADERS64*>(image+dos->e_lfanew);
    if(pe->Signature!=IMAGE_NT_SIGNATURE||pe->FileHeader.Machine!=IMAGE_FILE_MACHINE_AMD64||
        pe->FileHeader.TimeDateStamp!=0x6AA08464||pe->OptionalHeader.SizeOfImage!=0x1B11B000) return false;
    return bytes(RosterRva,std::to_array<unsigned char>({0x48,0x89,0x5c,0x24,0x08,0x55,0x56,0x57,0x41,0x54,0x41,0x55,0x41,0x56,0x41,0x57,0x48,0x8d,0x6c,0x24,0xd9,0x48,0x81,0xec})) &&
        bytes(MatchRva,std::to_array<unsigned char>({0x48,0x89,0x5c,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x48,0x89,0x7c,0x24,0x20,0x41,0x56,0x48,0x83,0xec,0x20,0x48,0x8b,0x1d})) &&
        bytes(AvailableRva,std::to_array<unsigned char>({0x40,0x53,0x48,0x83,0xec,0x30,0x48,0x8b,0xd9,0xe8,0x6a,0xf8,0xff,0xff,0x48,0x85,0xc0,0x74,0x15})) &&
        bytes(CompleteRva,std::to_array<unsigned char>({0x40,0x53,0x48,0x83,0xec,0x30,0x48,0x8b,0xd9,0xe8,0x76,0xe0,0xff,0xff,0x48,0x85,0xc0,0x74,0x13})) &&
        bytes(0x72702d8,std::to_array<unsigned char>({0x45,0x32,0xff,0x48,0x8d,0x93,0x98,0,0,0,0x48,0x8b,0xcb})) &&
        bytes(0x7270348,std::to_array<unsigned char>({0xe8,0x9f,0xf0,0x11,0xff,0x44,0x8a,0xf8})) &&
        bytes(0x7270379,std::to_array<unsigned char>({0x48,0x8d,0x93,0xa0,0,0,0})) &&
        bytes(0x727038a,std::to_array<unsigned char>({0xe8,0x69,0x02,0x0d,0xfb,0x41,0x3a,0xc7,0x0f,0x85,0xc6,0,0,0})) &&
        bytes(0x72703cd,std::to_array<unsigned char>({0xe8,0x26,0xd8,0x11,0xff,0x4d,0x8b,0xc4})) &&
        bytes(0x726e74b,std::to_array<unsigned char>({0x48,0x8b,0xc8,0x49,0x8d,0x96,0x90,0,0,0,0xe8,0x0a,0x20,0x0d,0xfb,0x44,0x8a,0xf0})) &&
        bytes(PreviewLockedRva,std::to_array<unsigned char>({0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x6c,0x24,0x10,0x48,0x89,0x74,0x24,0x18,0x57,0x48,0x83,0xec,0x20})) &&
        bytes(0x772096e,std::to_array<unsigned char>({0x48,0x8b,0x55,0x7f,0x48,0x8b,0xcb,0xe8,0xce,0xb5,0xfe,0xff,0x84,0xc0,0x74,0x05})) &&
        bytes(0x768bd87,std::to_array<unsigned char>({0x48,0x8b,0xc8,0x48,0x8d,0x93,0xa0,0x01,0,0,0xe8,0xce,0x49,0xcb,0xfa,0xeb,0x02})) &&
        bytes(ProgressEntryRva,std::to_array<unsigned char>({0x40,0x53,0x48,0x83,0xec,0x20,0x48,0x8b,0xda,0xe8,0x5e,0x2b,0xeb,0x03,0x48,0x85,0xc0,0x74,0x16}));
}
void stop() {
    ready=false;
    if(previewCallback!=Hook::ERROR_ID){Hook::UnregisterCallback(previewCallback);previewCallback=Hook::ERROR_ID;}
    if(suitLockCallback!=Hook::ERROR_ID){Hook::UnregisterCallback(suitLockCallback);suitLockCallback=Hook::ERROR_ID;}
    partyPinCall.reset();previewLockCall.reset();toggleCall.reset();availableCall.reset();matchCall.reset();
}
void start() {
    if(!supported()) {log(L"DISABLED: unrecognized executable or modified hook sites. Supported profile: Steam build 1344350. No patches applied.");return;}
    previewCallback=Hook::RegisterProcessEventPreCallback(
        [](Hook::TCallbackIterationData<void>&,UObject* context,UFunction* function,void* args){
            try{preview(context,function,args);}catch(const std::exception&){/* Fail closed. */}
        },{false,true,STR("LOTDKModeAccess"),STR("CrossModePreviewOnly")});
    if(previewCallback==Hook::ERROR_ID){log(L"DISABLED: could not register preview callback.");return;}
    suitLockCallback=Hook::RegisterProcessEventPostCallback(
        [](Hook::TCallbackIterationData<void>&,UObject* context,UFunction* function,void* args){
            try{unlockSuitButton(context,function,args);}catch(const std::exception&){}
        },{false,true,STR("LOTDKModeAccess"),STR("CrossModeSuitButtonsOnly")});
    if(suitLockCallback==Hook::ERROR_ID){stop();log(L"DISABLED: could not register suit-button callback.");return;}
    auto install=[&](ModeAccess::CallSite& call,const wchar_t* name,uintptr_t returnRva,uintptr_t targetRva,uintptr_t hook,bool mode=false){
        if(call.install(image+returnRva-5,image+targetRva,hook,mode)) return true;
        log(std::wstring(L"Setup failure: ")+name+L"; stage="+call.failure_stage()+L"; Win32="+std::to_wstring(call.system_error())+
            L"; allocation attempts="+std::to_wstring(call.allocation_attempts()));
        return false;
    };
    if(!install(matchCall,L"roster match",MatchReturnRva,MatchRva,reinterpret_cast<uintptr_t>(&matches),true)||
       !install(availableCall,L"roster availability",AvailableReturnRva,AvailableRva,reinterpret_cast<uintptr_t>(&available)) ||
       !install(toggleCall,L"party toggle",ToggleReturnRva,TagMatchesRva,reinterpret_cast<uintptr_t>(&toggleLocked)) ||
       !install(previewLockCall,L"preview lock",PreviewLockedReturnRva,PreviewLockedRva,reinterpret_cast<uintptr_t>(&previewLocked)) ||
       !install(partyPinCall,L"Batman party pin",PartyPinReturnRva,TagMatchesRva,reinterpret_cast<uintptr_t>(&partyPinned))) {
        stop();log(L"DISABLED: call-site setup failed; rolled back.");return;
    }
    ready=true;log(L"0.7.2 diagnostic ready (game 1344350). Detailed logging off unless requested.");
}
}

class LOTDKModeAccess final:public CppUserModBase {
public:
    LOTDKModeAccess(){ModName=STR("LOTDK Mode Access");ModVersion=STR("0.7.2-diagnostic");ModDescription=STR("Roster crossover, native previews and Mayhem party pin exception; build 1344350");ModAuthors=STR("Loomirr");}
    ~LOTDKModeAccess() override {stop();}
    void on_unreal_init() override {try{loadPolicies();start();}catch(const std::exception&){stop();log(L"DISABLED: initialization failed.");}}
    void on_lua_start(StringViewType name,LuaMadeSimple::Lua& lua,LuaMadeSimple::Lua&,LuaMadeSimple::Lua&,LuaMadeSimple::Lua*) override {
        if(name!=STR("LOTDKJokerHarleyNormal")&&name!=STR("LOTDKHeroesMayhem")) return;
        if(name==STR("LOTDKJokerHarleyNormal"))
            lua.register_function("LOTDK_SetCrossModeDebugLogging",[](const LuaMadeSimple::Lua& state)->int {villainsDebug=state.get_bool(1);return 0;});
        else lua.register_function("LOTDK_SetCrossModeDebugLogging",[](const LuaMadeSimple::Lua& state)->int {heroesDebug=state.get_bool(1);return 0;});
        if(name==STR("LOTDKJokerHarleyNormal"))
            lua.register_function("LOTDK_SetCrossModeAccess",[](const LuaMadeSimple::Lua& state)->int {
                villainsInNormal=state.get_bool(1);state.set_bool(ready);return 1;
            });
        else lua.register_function("LOTDK_SetCrossModeAccess",[](const LuaMadeSimple::Lua& state)->int {
                heroesInMayhem=state.get_bool(1);state.set_bool(ready);return 1;
            });
    }
    void on_lua_stop(StringViewType name,LuaMadeSimple::Lua&,LuaMadeSimple::Lua&,LuaMadeSimple::Lua&,LuaMadeSimple::Lua*) override {
        if(name==STR("LOTDKJokerHarleyNormal")) {villainsInNormal=false;villainsDebug=false;}
        if(name==STR("LOTDKHeroesMayhem")) {heroesInMayhem=false;heroesDebug=false;}
    }
};
extern "C" __declspec(dllexport) CppUserModBase* start_mod(){return new LOTDKModeAccess;}
extern "C" __declspec(dllexport) void uninstall_mod(CppUserModBase* mod){delete mod;}
