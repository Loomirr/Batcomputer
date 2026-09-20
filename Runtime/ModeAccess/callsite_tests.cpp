#define NOMINMAX
#include "CallSite.hpp"
#include <iostream>
#include <stdexcept>

int __fastcall changed(){return 99;}
int __fastcall mode_value(void*,void*,bool mode){return mode?21:20;}
int main(){
    int checks=0;
    auto check=[&](bool value,const char* message){if(!value)throw std::runtime_error(message);++checks;std::cout<<"PASS "<<message<<'\n';};
    auto* page=static_cast<uint8_t*>(VirtualAlloc(nullptr,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));
    check(page!=nullptr,"allocate private test code (no game process)");
    auto call=[&](size_t offset,size_t target){page[offset]=0xe8;int32_t delta=static_cast<int32_t>(target-offset-5);std::memcpy(page+offset+1,&delta,4);};
    // Two unrelated callers of the SAME native function.
    const uint8_t caller[]={0x48,0x83,0xec,0x28,0xe8,0,0,0,0,0x48,0x83,0xc4,0x28,0xc3};
    std::memcpy(page,caller,sizeof(caller));std::memcpy(page+32,caller,sizeof(caller));
    const uint8_t native[]={0xb8,17,0,0,0,0xc3};std::memcpy(page+128,native,sizeof(native));
    call(4,128);call(36,128);
    // Simulate the verified R15B mode register at the roster call site.
    const uint8_t modeCaller[]={0x41,0x57,0x48,0x83,0xec,0x20,0x44,0x8b,0xf9,0xe8,0,0,0,0,0x48,0x83,0xc4,0x20,0x41,0x5f,0xc3};
    std::memcpy(page+64,modeCaller,sizeof(modeCaller));call(73,128);
    DWORD ignored{};VirtualProtect(page,4096,PAGE_EXECUTE_READ,&ignored);FlushInstructionCache(GetCurrentProcess(),page,4096);
    auto first=reinterpret_cast<int(*)()>(page), second=reinterpret_cast<int(*)()>(page+32), direct=reinterpret_cast<int(*)()>(page+128);
    auto mode=reinterpret_cast<int(*)(int)>(page+64);
    auto address=reinterpret_cast<uintptr_t>(page);
    std::array<uint8_t,256> before{};std::memcpy(before.data(),page,before.size());
    ModeAccess::CallSite patch;
    check(first()==17 && second()==17 && direct()==17,"native baseline");
    check(!patch.install(address+4,address+129,reinterpret_cast<uintptr_t>(&changed)),"wrong expected target rejected");
    check(std::wstring_view(patch.failure_stage())==L"native CALL destination changed" && patch.allocation_attempts()==0,"wrong destination reports exact pre-allocation failure");
    check(!patch.install(address,address+128,reinterpret_cast<uintptr_t>(&changed)),"non-CALL opcode rejected");
    check(std::wstring_view(patch.failure_stage())==L"opcode changed","changed opcode has a distinct diagnostic");
    check(patch.install(address+4,address+128,reinterpret_cast<uintptr_t>(&changed)),"exact CALL patched");
    check(std::wstring_view(patch.failure_stage())==L"none" && patch.system_error()==0,"successful retry clears failure state");
    check(first()==99,"selected call uses replacement");
    check(second()==17 && direct()==17,"other callers and global native function unchanged");
    check(!patch.install(address+4,address+128,reinterpret_cast<uintptr_t>(&changed)),"double install rejected");
    check(std::memcmp(page,before.data(),4)==0 && std::memcmp(page+9,before.data()+9,247)==0,"no writes outside five-byte site");
    check(patch.reset() && first()==17,"restore native call");
    check(std::memcmp(page,before.data(),256)==0,"rollback byte-for-byte exact");
    check(patch.install(address+73,address+128,reinterpret_cast<uintptr_t>(&mode_value),true),"mode relay installed");
    check(mode(0)==20,"native normal-mode register forwarded");
    check(mode(1)==21,"native Mayhem-mode register forwarded");
    check(second()==17 && direct()==17,"mode relay does not alter unrelated queries");
    check(patch.reset() && mode(0)==17 && mode(1)==17,"mode relay restored");
    check(patch.reset(),"empty reset harmless");
    check(std::memcmp(page,before.data(),256)==0,"all private test code restored");
    VirtualFree(page,0,MEM_RELEASE);
    std::cout<<checks<<" call-site assertions passed.\n";
}
