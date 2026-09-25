// Offline native scorer for full materialized / pruned float models and the
// normalized union-vocabulary dynamic mixture. State ownership stays in Lua.
#include "mixture_budget.hpp"
extern "C" {
#include "lua.h"
#include "lauxlib.h"
}
using namespace mb;
static std::unique_ptr<Model> first,second,third;static double alpha=0;
struct State{uint16_t h[4]{};uint8_t n=0,pad=0;};
static int load(lua_State*L){try{
 std::ifstream f(luaL_checkstring(L,1));require(bool(f),"descriptor open");std::string mode,a,b,weight;std::getline(f,mode);std::getline(f,a);
 require(mode=="single"||mode=="mixture"||mode=="mixture3","descriptor mode");auto next=std::make_unique<Model>(a,false,true);std::unique_ptr<Model> other,extra;
 if(mode=="mixture"){std::getline(f,b);std::getline(f,weight);alpha=std::stod(weight);require(alpha>0&&alpha<1,"alpha");other=std::make_unique<Model>(b,false,true);require(next->vocab.words==other->vocab.words,"vocabulary mismatch");}
 if(mode=="mixture3"){
  std::getline(f,b);other=std::make_unique<Model>(b,false,true);std::getline(f,b);extra=std::make_unique<Model>(b,false,true);
  require(next->vocab.words==other->vocab.words&&next->vocab.words==extra->vocab.words,"vocabulary mismatch");
 }
 first=std::move(next);second=std::move(other);third=std::move(extra);State s{};s.h[0]=first->vocab.bos;s.n=1;lua_pushlstring(L,reinterpret_cast<const char*>(&s),sizeof s);return 1;
}catch(const std::exception&e){return luaL_error(L,"%s",e.what());}}
static int step(lua_State*L){try{
 size_t size;auto raw=luaL_checklstring(L,1,&size);require(first&&size==sizeof(State),"invalid state");State s{},out{};std::memcpy(&s,raw,size);require(s.n<=4,"state length");
 const char*t=luaL_checkstring(L,2);uint16_t target=(static_cast<unsigned char>(t[0])==3&&t[1]==0)?first->vocab.eos:first->vocab.id(t);
 uint16_t key[5];std::copy(s.h,s.h+s.n,key);key[s.n]=target;double p=first->extended_score(key,s.n+1);
 if(third)p=mix_three(p,second->extended_score(key,s.n+1),third->extended_score(key,s.n+1));
 else if(second)p=mix_log(p,second->extended_score(key,s.n+1),alpha);
 out.n=std::min<int>(4,s.n+1);std::copy(key+s.n+1-out.n,key+s.n+1,out.h);
 lua_pushnumber(L,p*std::log(10.));lua_pushlstring(L,reinterpret_cast<const char*>(&out),sizeof out);return 2;
}catch(const std::exception&e){return luaL_error(L,"%s",e.what());}}
extern "C" int luaopen_shape5(lua_State*L){const luaL_Reg f[]={{"load",load},{"step",step},{nullptr,nullptr}};luaL_newlib(L,f);return 1;}
