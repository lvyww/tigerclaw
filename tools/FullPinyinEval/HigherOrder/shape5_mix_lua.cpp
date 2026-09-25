// Offline probability interpolation; separate vocabularies and full states.
#include "lm/model.hh"
#include <memory>
#include <vector>
#include <cstring>
#include <cmath>
#include <cstdlib>
#include <algorithm>
extern "C" {
#include "lua.h"
#include "lauxlib.h"
}
static std::unique_ptr<lm::base::Model> old_model, new_model;
static double weight;
static int load(lua_State* L) {
 const char* path=luaL_checkstring(L,1);
 try {
  const char* old_path=std::getenv("SHAPE5_OLD_MODEL");
  const char* value=std::getenv("SHAPE5_OLD_WEIGHT"); char* end=nullptr;
  if(!old_path || !value) throw std::runtime_error("missing mixture configuration");
  weight=std::strtod(value,&end);
  if(end==value || *end || !std::isfinite(weight) || weight<0 || weight>1) throw std::runtime_error("invalid mixture weight");
  lm::ngram::Config config;config.load_method=util::LAZY;config.show_progress=false;
  old_model.reset(lm::ngram::LoadVirtual(old_path,config));
  new_model.reset(lm::ngram::LoadVirtual(path,config));
  if(old_model->Order()!=5 || new_model->Order()!=5) throw std::runtime_error("expected two order-five models");
  std::vector<char> state(old_model->StateSize()+new_model->StateSize(),0);
  old_model->BeginSentenceWrite(state.data());new_model->BeginSentenceWrite(state.data()+old_model->StateSize());
  lua_pushlstring(L,state.data(),state.size());return 1;
 }catch(const std::exception& e){return luaL_error(L,"%s",e.what());}
}
static int step(lua_State* L) {
 size_t length;const char* state=luaL_checklstring(L,1,&length);const char* token=luaL_checkstring(L,2);
 if(!old_model || !new_model || length!=old_model->StateSize()+new_model->StateSize()) return luaL_error(L,"invalid mixture state");
 try {
  std::vector<char> before(length),after(length,0);std::memcpy(before.data(),state,length);
  auto score=[&](lm::base::Model& model,size_t offset){
   auto id=static_cast<unsigned char>(token[0])==3 && token[1]==0 ? model.BaseVocabulary().EndSentence():model.BaseVocabulary().Index(token);
   return model.BaseScore(before.data()+offset,id,after.data()+offset)*std::log(10.0);
  };
  double a=score(*old_model,0),b=score(*new_model,old_model->StateSize()),result;
  if(weight==1)result=a;else if(weight==0)result=b;
  else {a+=std::log(weight);b+=std::log1p(-weight);double m=std::max(a,b);result=std::isinf(m)?m:m+std::log(std::exp(a-m)+std::exp(b-m));}
  lua_pushnumber(L,result);lua_pushlstring(L,after.data(),after.size());return 2;
 }catch(const std::exception& e){return luaL_error(L,"%s",e.what());}
}
extern "C" int luaopen_shape5(lua_State* L){const luaL_Reg funcs[]={{"load",load},{"step",step},{nullptr,nullptr}};luaL_newlib(L,funcs);return 1;}
