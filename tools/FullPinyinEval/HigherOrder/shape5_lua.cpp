// Offline Lua 5.4 adapter. Each path carries the model's complete opaque state.
#include "lm/model.hh"
#include <memory>
#include <vector>
#include <cstring>
#include <cmath>
extern "C" {
#include "lua.h"
#include "lauxlib.h"
}
static std::unique_ptr<lm::base::Model> model;
static int load(lua_State* L) {
 const char* path=luaL_checkstring(L,1);
 try {lm::ngram::Config config;config.load_method=util::LAZY;config.show_progress=false;
 model.reset(lm::ngram::LoadVirtual(path,config));
 if(model->Order()!=5)throw std::runtime_error("expected order five");
 std::vector<char> state(model->StateSize(),0);model->BeginSentenceWrite(state.data());
 lua_pushlstring(L,state.data(),state.size());return 1;
 }catch(const std::exception& e){return luaL_error(L,"%s",e.what());}
}
static int step(lua_State* L) {
 size_t length;const char* state=luaL_checklstring(L,1,&length);const char* token=luaL_checkstring(L,2);
 if(!model || length!=model->StateSize())return luaL_error(L,"invalid fivegram state");
 try {std::vector<char> before(length),after(length,0);std::memcpy(before.data(),state,length);
 const auto id=static_cast<unsigned char>(token[0])==3 && token[1]==0 ? model->BaseVocabulary().EndSentence():model->BaseVocabulary().Index(token);
 double score=model->BaseScore(before.data(),id,after.data())*std::log(10.0);
 lua_pushnumber(L,score);lua_pushlstring(L,after.data(),after.size());return 2;
 }catch(const std::exception& e){return luaL_error(L,"%s",e.what());}
}
extern "C" int luaopen_shape5(lua_State* L){const luaL_Reg funcs[]={{"load",load},{"step",step},{nullptr,nullptr}};luaL_newlib(L,funcs);return 1;}
