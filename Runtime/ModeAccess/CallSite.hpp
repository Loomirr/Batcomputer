#pragma once
#include <Windows.h>
#include <array>
#include <cstdint>
#include <cstring>
#include <limits>

// Redirect exactly one verified E8 call, never the callee's global entry point.
// Install/uninstall only during startup/shutdown, not by live Lua reload.
namespace ModeAccess {
class CallSite {
    uint8_t* site{};
    void* relay{};
    std::array<uint8_t,5> original{}, replacement{};
    bool installed{};
    const wchar_t* failure=L"none";
    DWORD systemError{};
    size_t allocationAttempts{};
    bool fail(const wchar_t* reason,DWORD error=0){failure=reason;systemError=error;return false;}
    static bool write(uint8_t* target,const std::array<uint8_t,5>& value) {
        DWORD protection{};
        if(!VirtualProtect(target,value.size(),PAGE_EXECUTE_READWRITE,&protection)) return false;
        std::memcpy(target,value.data(),value.size());
        FlushInstructionCache(GetCurrentProcess(),target,value.size());
        DWORD ignored{};
        VirtualProtect(target,value.size(),protection,&ignored);
        return true;
    }
public:
    const wchar_t* failure_stage() const{return failure;}
    DWORD system_error() const{return systemError;}
    size_t allocation_attempts() const{return allocationAttempts;}
    CallSite()=default;
    CallSite(const CallSite&)=delete;
    CallSite& operator=(const CallSite&)=delete;
    ~CallSite(){reset();}
    bool install(uintptr_t address,uintptr_t nativeTarget,uintptr_t hook,bool passRosterMode=false) {
        failure=L"none";systemError=0;allocationAttempts=0;
        if(site || !address || !nativeTarget || !hook) return fail(L"invalid arguments/already installed");
        auto* target=reinterpret_cast<uint8_t*>(address);
        if(target[0]!=0xE8) return fail(L"opcode changed");
        int32_t oldDisplacement{};std::memcpy(&oldDisplacement,target+1,4);
        if(static_cast<uintptr_t>(static_cast<int64_t>(address+5)+oldDisplacement)!=nativeTarget) return fail(L"native CALL destination changed");
        SYSTEM_INFO info{};GetSystemInfo(&info);
        const uintptr_t step=info.dwAllocationGranularity, origin=address&~(step-1);
        for(uintptr_t distance=step;!relay && distance<0x7fff0000;distance+=step) {
            for(bool below:{true,false}) {
                if(below && origin<distance+step) continue;
                auto candidate=below?origin-distance:origin+distance;
                if(candidate<reinterpret_cast<uintptr_t>(info.lpMinimumApplicationAddress) ||
                   candidate>reinterpret_cast<uintptr_t>(info.lpMaximumApplicationAddress)-4096) continue;
                ++allocationAttempts;
                relay=VirtualAlloc(reinterpret_cast<void*>(candidate),4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);
                if(!relay) systemError=GetLastError();
                if(relay) break;
            }
        }
        if(!relay) return fail(L"near relay allocation",systemError);
        auto delta=static_cast<int64_t>(reinterpret_cast<uintptr_t>(relay))-static_cast<int64_t>(address+5);
        if(delta<INT32_MIN || delta>INT32_MAX){VirtualFree(relay,0,MEM_RELEASE);relay=nullptr;return fail(L"relay outside CALL range");}
        auto* code=static_cast<uint8_t*>(relay);
        size_t offset=0;
        // R15B is the roster's native Mayhem boolean immediately before MatchesAny.
        // R8 is volatile and is not live across this specific original call site.
        if(passRosterMode){code[0]=0x45;code[1]=0x8a;code[2]=0xc7;offset=3;} // mov r8b,r15b
        code[offset]=0xff;code[offset+1]=0x25; // jmp qword ptr [rip+0], preserves registers
        std::memset(code+offset+2,0,4);std::memcpy(code+offset+6,&hook,8);
        DWORD ignored{};
        if(!VirtualProtect(relay,4096,PAGE_EXECUTE_READ,&ignored)){auto error=GetLastError();VirtualFree(relay,0,MEM_RELEASE);relay=nullptr;return fail(L"relay executable protection",error);}
        FlushInstructionCache(GetCurrentProcess(),relay,offset+14);
        std::memcpy(original.data(),target,5);
        replacement[0]=0xe8;auto relative=static_cast<int32_t>(delta);std::memcpy(replacement.data()+1,&relative,4);
        site=target;
        if(!write(site,replacement)){auto error=GetLastError();site=nullptr;VirtualFree(relay,0,MEM_RELEASE);relay=nullptr;return fail(L"CALL-site write protection",error);}
        installed=true;systemError=0;return true;
    }
    bool reset() {
        if(installed) {
            // Do not overwrite another mod's subsequent patch or free a live relay.
            if(std::memcmp(site,replacement.data(),5)!=0 || !write(site,original)) return false;
            installed=false;
        }
        if(relay){VirtualFree(relay,0,MEM_RELEASE);relay=nullptr;}
        site=nullptr;return true;
    }
};
}
