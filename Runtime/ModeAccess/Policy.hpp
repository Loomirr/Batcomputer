#pragma once
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <set>
#include <cstdint>

namespace ModeAccess {
enum class Mode { Normal, Mayhem, Both, Conflict };
struct Decision { bool include; bool crossModeUnlock; };
// One matching availability call per roster iteration, keyed to the native
// caller's stack slot. A different/nested caller fails closed, never inherits it.
struct PendingAvailability {
    uintptr_t callerSlot{};
    bool unlock{};
    void set(uintptr_t slot,bool value){callerSlot=slot;unlock=value;}
    bool take(uintptr_t slot){const bool value=slot==callerSlot && unlock;callerSlot=0;unlock=false;return value;}
};
inline wchar_t folded(wchar_t c) { return c>=L'A'&&c<=L'Z'?c+(L'a'-L'A'):c; }
struct ScopeLess {
    using is_transparent = void;
    bool operator()(std::wstring_view a, std::wstring_view b) const {
        for(size_t i=0;i<a.size()&&i<b.size();++i) if(folded(a[i])!=folded(b[i])) return folded(a[i])<folded(b[i]);
        return a.size()<b.size();
    }
};
using PolicyMap = std::map<std::wstring,Mode,ScopeLess>;
inline bool equal(std::wstring_view a,std::wstring_view b) { return !ScopeLess{}(a,b)&&!ScopeLess{}(b,a); }
inline bool within(std::wstring_view tag, std::wstring_view scope) {
    return equal(tag,scope) || (tag.size() > scope.size() && equal(tag.substr(0,scope.size()),scope) && tag[scope.size()] == L'.');
}
inline bool valid_scope(std::wstring_view scope) {
    constexpr std::wstring_view prefix = L"Pawns.Playable.";
    if (scope.size()<prefix.size() || !equal(scope.substr(0,prefix.size()),prefix)) return false;
    auto id = scope.substr(prefix.size());
    auto letter=[](wchar_t c){return (c>=L'A'&&c<=L'Z')||(c>=L'a'&&c<=L'z');};
    if (id.empty() || id.size()>64 || !letter(id.front())) return false;
    for(auto c:id) if(!letter(c) && !(c>=L'0'&&c<=L'9')) return false;
    return true;
}
inline std::optional<Decision> decide(std::wstring_view tag, bool mayhem,
    const PolicyMap& policies, bool villainsInNormal, bool heroesInMayhem) {
    if (tag.size()<15 || !equal(tag.substr(0,15),L"Pawns.Playable.")) return {};
    auto end=tag.find(L'.',15); // strlen("Pawns.Playable.") == 15
    auto scope=std::wstring(tag.substr(0,end));
    if(auto found=policies.find(scope);found!=policies.end()) {
        if(found->second==Mode::Conflict) return {};
        return Decision{found->second==Mode::Both || (found->second==Mode::Mayhem)==mayhem,false};
    }
    if(!mayhem && villainsInNormal && (within(tag,L"Pawns.Playable.Joker")||within(tag,L"Pawns.Playable.HarleyQuinn")))
        return Decision{true,true};
    static const std::set<std::wstring,ScopeLess> heroes={L"Batman",L"BruceWayne",L"Gordon",L"Catwoman",L"RobinDickGrayson",L"Batgirl",L"Nightwing",L"TaliaAlGhul",L"TaliaAlGhulHelmet",L"TaliaAlGhulPrologue",L"ThomasWayne",L"Alfred",L"LuciusFox"};
    if(mayhem && heroesInMayhem && heroes.contains(std::wstring_view(scope).substr(15))) return Decision{true,true};
    return {};
}
inline bool allow_cross_mode_preview(std::wstring_view tag,bool mayhem,const PolicyMap& policies,
    bool villainsInNormal,bool heroesInMayhem) {
    const auto decision=decide(tag,mayhem,policies,villainsInNormal,heroesInMayhem);
    return decision && decision->include && decision->crossModeUnlock;
}
inline bool allow_batman_toggle(bool mayhem,bool heroesEnabled,std::wstring_view pawn,std::wstring_view configuredGroup) {
    return mayhem && heroesEnabled && equal(configuredGroup,L"Pawns.Playable.Batman") && within(pawn,L"Pawns.Playable.Batman");
}
inline bool preview_locked_result(bool nativeLocked,bool validContext,std::wstring_view requestedTag,
    bool mayhem,const PolicyMap& policies,bool villainsInNormal,bool heroesInMayhem) {
    return nativeLocked && !(validContext && allow_cross_mode_preview(requestedTag,mayhem,policies,villainsInNormal,heroesInMayhem));
}
inline bool party_pin_result(bool nativePinned,bool modeKnown,bool mayhem,bool heroesEnabled,
    std::wstring_view pawn,std::wstring_view configuredGroup) {
    return nativePinned && !(modeKnown && allow_batman_toggle(mayhem,heroesEnabled,pawn,configuredGroup));
}
inline std::wstring preview_selection(bool suitActive,std::wstring_view suitTag,bool mainActive,std::wstring_view mainTag) {
    // No fallback from an unresolved active suit to a previous character.
    return std::wstring(suitActive?suitTag:mainActive?mainTag:std::wstring_view{});
}
}
