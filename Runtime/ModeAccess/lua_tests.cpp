#include <lua.hpp>
#include <iostream>
#include <stdexcept>
#include <fstream>
#include <sstream>
#include <string>
static void run(lua_State* state,const std::string& code) {
    if(luaL_dostring(state,code.c_str())!=LUA_OK)throw std::runtime_error(lua_tostring(state,-1));
}
int main(int argc,char** argv) {
    int checks{};
    for(int i=1;i<argc;++i) for(int scenario=0;scenario<7;++scenario) {
        std::ifstream file(argv[i]);if(!file)throw std::runtime_error("missing Lua script");
        std::stringstream buffer;buffer<<file.rdbuf();auto source=buffer.str();
        if(scenario==5)source.replace(source.find("ENABLED_ON_STARTUP = true"),std::string("ENABLED_ON_STARTUP = true").size(),"ENABLED_ON_STARTUP = false");
        if(scenario==6)source.replace(source.find("DEBUG_LOGGING = false"),std::string("DEBUG_LOGGING = false").size(),"DEBUG_LOGGING = true");
        lua_State* state=luaL_newstate();luaL_openlibs(state);
        run(state,R"(
            requests={}; messages={}; bindings={}; debugValue=nil
            print=function(message) table.insert(messages,message) end
            Key={F9=120,F10=121}
            RegisterKeyBind=function(key,callback)
                assert(type(callback)=='function')
                assert(bindings[key]==nil)
                bindings[key]=callback
            end
            IsKeyBindRegistered=function() return false end
            LOTDK_SetCrossModeDebugLogging=function(value) debugValue=value end
        )");
        if(scenario>0)run(state,std::string("LOTDK_SetCrossModeAccess=function(value) assert(type(value)=='boolean'); table.insert(requests,value); return ")+(scenario==1?"false":"true")+" end");
        if(scenario==3)run(state,"IsKeyBindRegistered=function() return true end");
        if(scenario==4)run(state,"RegisterKeyBind=nil; Key=nil; ModifierKey=nil");
        run(state,source);
        const int key=std::string(argv[i]).find("HeroesMayhem")!=std::string::npos?121:120;
        if(scenario==0)run(state,"assert(#requests==0 and next(bindings)==nil and #messages==1 and debugValue==nil)");
        else {
            run(state,std::string("assert(#requests==1 and requests[1]==")+(scenario==5?"false":"true")+" and debugValue=="+(scenario==6?"true":"false")+")");
            if(scenario==3 || scenario==4)run(state,"assert(next(bindings)==nil and #messages==2)");
            else {
                run(state,"assert(#messages==1 and type(bindings["+std::to_string(key)+"])=='function')");
                run(state,"bindings["+std::to_string(key)+"](); bindings["+std::to_string(key)+"](); assert(#requests==3 and requests[2]~=requests[1] and requests[3]==requests[1] and #messages==3)");
                run(state,"assert(string.find(messages[3],'Return to title',1,true))");
            }
        }
        lua_close(state);++checks;
    }
    std::cout<<checks<<" Lua scenarios passed (toggles, quiet logging, debug opt-in, startup-off, missing/unsupported helper, missing/conflicting shortcuts).\n";
}
