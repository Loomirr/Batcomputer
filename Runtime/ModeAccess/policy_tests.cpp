#include "Policy.hpp"
#include <iostream>
#include <stdexcept>
using namespace ModeAccess;
int main() {
    int checks=0;
    auto check=[&](bool ok,const char* text){if(!ok)throw std::runtime_error(text);++checks;std::cout<<"PASS "<<text<<'\n';};
    PolicyMap policies{{L"Pawns.Playable.TestNormal",Mode::Normal},{L"Pawns.Playable.TestMayhem",Mode::Mayhem},{L"Pawns.Playable.TestBoth",Mode::Both}};
    for(bool mayhem:{false,true}) {
        check(decide(L"Pawns.Playable.TestNormal.Default",mayhem,policies,false,false)->include==!mayhem,"normal policy");
        check(decide(L"Pawns.Playable.TestMayhem.Default",mayhem,policies,false,false)->include==mayhem,"mayhem policy");
        check(decide(L"Pawns.Playable.TestBoth.OtherSuit",mayhem,policies,false,false)->include,"both policy and child suit");
        check(!decide(L"Pawns.Playable.Batman.Default",mayhem,policies,false,false),"unrequested native character preserved");
    }
    check(decide(L"Pawns.Playable.Joker.HTV",false,policies,true,false)->crossModeUnlock,"Joker normal roster availability only");
    check(decide(L"Pawns.Playable.HarleyQuinn.BTAS",false,policies,true,false)->include,"Harley normal");
    check(!decide(L"Pawns.Playable.Joker.HTV",true,policies,true,false),"Joker native Mayhem unchanged");
    for(auto family:{L"Batman",L"BruceWayne",L"Gordon",L"Catwoman",L"RobinDickGrayson",L"Batgirl",L"Nightwing",L"TaliaAlGhul",L"TaliaAlGhulHelmet",L"TaliaAlGhulPrologue",L"ThomasWayne",L"Alfred",L"LuciusFox"}) {
        auto tag=std::wstring(L"Pawns.Playable.")+family+L".Default";
        check(decide(tag,true,policies,false,true)->include,"base-game playable Mayhem");
        check(!decide(tag,false,policies,false,true),"base-game normal unchanged");
    }
    check(!decide(L"Pawns.NPC.Joker",false,policies,true,true),"NPCs excluded");
    check(!decide(L"Pawns.Vehicle.Batmobile",true,policies,true,true),"vehicles excluded");
    check(!decide(L"Pawns.Playable.JokerImposter.Default",false,policies,true,true),"prefix collision excluded");
    check(!decide(L"Pawns.Playable.TestNormal.Default",true,policies,true,true)->include,"authored policy beats optional global mods");
    check(decide(L"pawns.playable.TESTBOTH.Default",true,policies,false,false)->include,"FName policy comparison ignores case");
    check(decide(L"pawns.playable.batman.Default",true,policies,false,true)->include,"FName native family comparison ignores case");
    check(!policies.emplace(L"pawns.playable.testboth",Mode::Normal).second,"case-only duplicates cannot bypass conflict detection");
    policies[L"Pawns.Playable.TestBoth"]=Mode::Conflict;
    check(!decide(L"Pawns.Playable.TestBoth.Default",true,policies,true,true),"conflict retains native behavior");
    check(valid_scope(L"Pawns.Playable.RiddlerTest"),"valid custom scope");
    for(auto scope:{L"Pawns.Playable.",L"Pawns.Playable.9bad",L"Pawns.Playable.Riddler.Default",L"Pawns.Playable",L"Pawns.NPC.Joker",L"../foo"}) check(!valid_scope(scope),"invalid scope rejected");
    check(allow_cross_mode_preview(L"Pawns.Playable.Joker.Default",false,policies,true,false),"Joker cross-mode preview allowed");
    check(allow_cross_mode_preview(L"Pawns.Playable.Batman.Default",true,policies,false,true),"Batman cross-mode preview allowed");
    check(!allow_cross_mode_preview(L"Pawns.Playable.Joker.Default",true,policies,true,true),"native Mayhem Joker locks unchanged");
    check(!allow_cross_mode_preview(L"Pawns.Playable.Batman.Default",false,policies,true,true),"native normal Batman locks unchanged");
    check(!allow_cross_mode_preview(L"Pawns.Playable.Joker.Default",false,policies,false,true),"disabled villain script preserves preview");
    check(!allow_cross_mode_preview(L"Pawns.Vehicle.Batmobile",true,policies,true,true),"vehicle previews untouched");
    check(!allow_cross_mode_preview(L"Pawns.Playable.TestNormal.Default",true,policies,true,true),"custom excluded preview remains excluded");
    for(auto family:{L"Joker",L"HarleyQuinn",L"Batman",L"Catwoman",L"RobinDickGrayson"}) {
        const bool mayhem=std::wstring_view(family)!=L"Joker" && std::wstring_view(family)!=L"HarleyQuinn";
        const auto tag=std::wstring(L"Pawns.Playable.")+family+L".UnselectedSuit";
        check(!preview_locked_result(true,true,tag,mayhem,policies,true,true),"unselected crossover preview is not silhouetted");
        check(preview_locked_result(true,false,tag,mayhem,policies,true,true),"invalid preview context cannot unlock material");
        check(preview_locked_result(true,true,tag,!mayhem,policies,true,true),"same-mode suit preview locks preserved");
        check(preview_locked_result(true,true,tag,mayhem,policies,false,false),"disabled switches preserve native preview lock");
        check(!preview_locked_result(false,true,tag,mayhem,policies,false,false),"already visible preview never relocked");
    }
    check(preview_locked_result(true,true,L"",false,policies,true,true),"missing requested metadata fails closed");
    check(preview_locked_result(true,true,L"Pawns.Vehicle.Batmobile",true,policies,true,true),"native preview query leaves vehicles unchanged");
    check(preview_locked_result(true,true,L"Pawns.Playable.TestNormal.Default",true,policies,true,true),"native preview query respects authored mode exclusion");
    PendingAvailability pending;
    check(preview_selection(false,L"old suit",true,L"new group")==L"new group","inactive suit menu cannot authorize stale preview");
    check(preview_selection(true,L"new suit",true,L"old group")==L"new suit","active suit selection takes priority");
    check(preview_selection(true,L"",true,L"old group").empty(),"unresolved active suit fails closed");
    check(preview_selection(false,L"old suit",false,L"old group").empty(),"no active character menu means no preview override");
    for(auto family:{L"Joker",L"HarleyQuinn"}) for(auto variant:{L"Default",L"BTAS",L"VillyAwards",L"OtherRegisteredSuit"}) {
        check(allow_cross_mode_preview(std::wstring(L"Pawns.Playable.")+family+L"."+variant,false,policies,true,false),"every registered villain suit is eligible in normal crossover");
    }
    check(allow_batman_toggle(true,true,L"Pawns.Playable.Batman.AnimatedSeries",L"Pawns.Playable.Batman"),"Mayhem Batman can leave toggle slot");
    check(!allow_batman_toggle(false,true,L"Pawns.Playable.Batman.AnimatedSeries",L"Pawns.Playable.Batman"),"normal Batman party restriction unchanged");
    check(!allow_batman_toggle(true,false,L"Pawns.Playable.Batman.AnimatedSeries",L"Pawns.Playable.Batman"),"disabled Mayhem script preserves party restriction");
    check(!allow_batman_toggle(true,true,L"Pawns.Playable.BatmanImposter.Default",L"Pawns.Playable.Batman"),"Batman prefix collision not exempted");
    check(!allow_batman_toggle(true,true,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Joker"),"different configured party lock untouched");
    check(!party_pin_result(true,true,true,true,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Batman"),"Mayhem party preferences do not pin Batman");
    check(party_pin_result(true,true,false,true,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Batman"),"normal-mode preference pin preserved");
    check(party_pin_result(true,false,true,true,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Batman"),"unknown current mode preserves preference pin");
    check(party_pin_result(true,true,true,false,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Batman"),"disabled heroes switch preserves preference pin");
    check(party_pin_result(true,true,true,true,L"Pawns.Playable.Joker.Default",L"Pawns.Playable.Batman"),"non-Batman preference comparison preserved");
    check(party_pin_result(true,true,true,true,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Joker"),"other configured pin preserved");
    check(party_pin_result(true,true,true,true,L"Pawns.Playable.BatmanImposter.Default",L"Pawns.Playable.Batman"),"preference pin family boundary checked");
    check(!party_pin_result(false,true,false,true,L"Pawns.Playable.Batman.Default",L"Pawns.Playable.Batman"),"native unpinned never becomes pinned");
    for(bool mayhem:{true,false,true,false})check(party_pin_result(true,true,mayhem,true,L"Pawns.Playable.Batman.OtherSuit",L"Pawns.Playable.Batman")==!mayhem,"mode transitions cannot retain previous pin override");
    check(!pending.take(100),"no implicit availability override");
    pending.set(100,true);check(pending.take(100),"same roster iteration consumes override");
    check(!pending.take(100),"availability override is one-shot");
    pending.set(100,true);check(!pending.take(200),"different/nested caller cannot inherit override");
    check(!pending.take(100),"mismatched caller clears stale override");
    pending.set(100,true);pending.set(100,false);check(!pending.take(100),"next native character clears previous crossover");
    std::cout<<checks<<" policy assertions passed. No game process used.\n";
}
