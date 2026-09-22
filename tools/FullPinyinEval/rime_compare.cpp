// Isolated real-Rime benchmark. TSV input: id, raw code. Never commits/learns.
#include <rime_api.h>
#include <dlfcn.h>
#include <chrono>
#include <cstdlib>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>

using Clock = std::chrono::steady_clock;
static double elapsed(Clock::time_point t) {
  return std::chrono::duration<double, std::milli>(Clock::now()-t).count();
}
static std::string quote(const std::string& s) {
  std::string o="\"";
  for (unsigned char c:s) {
    if(c=='"'||c=='\\') {o+='\\';o+=c;}
    else if(c<32) { const char* h="0123456789abcdef";o+="\\u00";o+=h[c>>4];o+=h[c&15]; }
    else o+=c;
  }
  return o+'"';
}
int main(int argc,char**argv) {
  RimeApi* api=nullptr; RimeSessionId session=0;
  try {
    if(argc!=6) throw std::runtime_error("user-dir shared-dir cases.tsv output.jsonl decode|keys|bench");
    if(!dlopen("/usr/lib64/rime-plugins/librime-lua.so",RTLD_NOW|RTLD_GLOBAL)) throw std::runtime_error(dlerror());
    const char* grammar=std::getenv("RIME_EVAL_OCTAGRAM");
    if(grammar && !dlopen(grammar,RTLD_NOW|RTLD_GLOBAL))throw std::runtime_error(dlerror());
    api=rime_get_api(); const char* modules[]={"default","lua",grammar?"octagram":nullptr,nullptr};
    RIME_STRUCT(RimeTraits,t);t.user_data_dir=argv[1];t.shared_data_dir=argv[2];t.log_dir=argv[1];
    t.app_name="rime.fullpinyin.eval";t.modules=modules;
    api->setup(&t);api->initialize(&t);
    if(api->start_maintenance(False))api->join_maintenance_thread();
    session=api->create_session();
    const char* schema=std::getenv("RIME_EVAL_SCHEMA");
    if(!schema)schema="wanxiang";
    if(!session||!api->select_schema(session,schema))throw std::runtime_error("schema deployment failed");
    std::cerr<<"librime="<<api->get_version()<<"\n";
    api->set_option(session,"ascii_mode",False);
    std::ifstream in(argv[3]);std::ofstream out(argv[4]);
    if(!in||!out)throw std::runtime_error("file open failed");
    bool bench=std::string(argv[5])=="bench";std::string line;int count=0;
    while(std::getline(in,line)) {
      auto tab=line.find('\t');if(tab==std::string::npos)throw std::runtime_error("bad TSV");
      std::string id=line.substr(0,tab),code=line.substr(tab+1);
      api->clear_composition(session);
      auto start=Clock::now();
      if(bench) {
        auto key=[&](int keycode,const char* direction,int keys) {
          auto kt=Clock::now();api->process_key(session,keycode,0);
          RIME_STRUCT(RimeContext,c);if(api->get_context(session,&c))api->free_context(&c);
          double ms=elapsed(kt);
          out<<"{\"id\":"<<quote(id)<<",\"direction\":"<<quote(direction)<<",\"keys\":"<<keys<<",\"ms\":"<<ms<<"}\n";
        };
        for(size_t i=0;i<code.size();++i)key(code[i],"append",i+1);
        if(std::string(api->get_input(session))!=code)throw std::runtime_error("raw input mismatch");
        for(size_t i=code.size();i>0;--i)key(0xff08,"backspace",i-1);
        if(std::string(api->get_input(session))!="")throw std::runtime_error("backspace left input");
      } else {
        if(std::string(argv[5])=="keys") {
          for(unsigned char c:code)api->process_key(session,c,0);
        } else if(!api->set_input(session,code.c_str()))throw std::runtime_error("set_input failed");
        RIME_STRUCT(RimeContext,c);if(!api->get_context(session,&c))throw std::runtime_error("missing context");
        std::string preedit=c.composition.preedit?c.composition.preedit:"";
        api->free_context(&c);double firstMs=elapsed(start);
        if(std::string(api->get_input(session))!=code)throw std::runtime_error("raw input mismatch");
        out<<"{\"id\":"<<quote(id)<<",\"code\":"<<quote(code)<<",\"firstPageMs\":"<<firstMs
           <<",\"preedit\":"<<quote(preedit)<<",\"candidates\":[";
        RimeCandidateListIterator it{};int n=0;
        if(api->candidate_list_begin(session,&it)) {
          while(n<50 && api->candidate_list_next(&it)) {
            if(n++)out<<',';
            out<<"{\"text\":"<<quote(it.candidate.text?it.candidate.text:"")
               <<",\"boundary\":"<<quote(it.candidate.comment?it.candidate.comment:"")<<'}';
          }
          api->candidate_list_end(&it);
        }
        out<<"],\"totalMs\":"<<elapsed(start)<<"}\n";
      }
      RIME_STRUCT(RimeCommit,c);if(api->get_commit(session,&c)) {
        api->free_commit(&c);throw std::runtime_error("unexpected commit");
      }
      if(++count%100==0){out.flush();std::cerr<<count<<" cases\n";}
    }
    api->destroy_session(session);session=0;api->finalize();return 0;
  }catch(const std::exception&e){std::cerr<<e.what()<<'\n';if(session)api->destroy_session(session);if(api)api->finalize();return 1;}
}
