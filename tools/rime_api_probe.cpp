// Headless real librime + librime-lua integration. No desktop IME is installed.
#include <rime_api.h>
#include <dlfcn.h>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
RimeApi* api=nullptr;
RimeSessionId session=0;
unsigned checks=0;
std::string committed;
void check(bool ok,const char* message){++checks;if(!ok)throw std::runtime_error(message);}
std::string input(){const auto p=api->get_input(session);return p?p:"";}
void drain(){RIME_STRUCT(RimeCommit,c);if(api->get_commit(session,&c)){if(c.text)committed+=c.text;api->free_commit(&c);}}
void key(int code,int modifiers=0){check(api->process_key(session,code,modifiers),"Rime did not consume expected key");drain();}
void type(const std::string& text){for(unsigned char c:text)key(c);}
void reset(bool automatic){api->clear_composition(session);drain();committed.clear();api->set_option(session,"ascii_mode",False);api->set_option(session,"tiger_sentence_early_commit",automatic?True:False);}
std::string property(const char* name){char data[4096]={};api->get_property(session,name,data,sizeof(data));return data;}
int selection(){RIME_STRUCT(RimeContext,c);check(api->get_context(session,&c),"Missing Rime context");const int i=c.menu.page_no*c.menu.page_size+c.menu.highlighted_candidate_index;api->free_context(&c);return i;}
void locked(){reset(false);type("ab");key(0xff09);type("cd");check(!property("tiger_sentence_locks").empty(),"Real Tab did not lock candidate");check(input()=="abcd" && committed.empty(),"Tab lock unexpectedly committed");}
}
int main(int argc,char**argv){
    try{
        check(argc==4,"Usage: rime_api_probe <isolated-user-dir> <shared-data-dir> <lua-plugin>");
        api=rime_get_api();
        check(dlopen(argv[3],RTLD_NOW|RTLD_GLOBAL)!=nullptr,"Cannot load librime-lua plugin");
        const char* modules[]={"default","lua",nullptr};
        RIME_STRUCT(RimeTraits,traits);
        traits.shared_data_dir=argv[2];traits.user_data_dir=argv[1];traits.log_dir=argv[1];
        traits.app_name="rime.tiger.regression";traits.modules=modules;
        api->setup(&traits);api->initialize(&traits);
        if(api->start_maintenance(True))api->join_maintenance_thread();
        session=api->create_session();check(session!=0,"Cannot create Rime session");
        check(api->select_schema(session,"tiger_sentence"),"Cannot deploy/select production schema");
        check(RIME_API_AVAILABLE(api,get_input) && RIME_API_AVAILABLE(api,set_caret_pos),"Required Rime API unavailable");
        reset(true);type("vp");api->set_caret_pos(session,1);key('a');
        check(input()=="vap" && api->get_caret_pos(session)==2 && committed.empty(),"Real middle insertion committed or replaced the suffix");
        reset(true);type("vpa");check(committed=="刘" && input()=="a","Real end append no longer commits unique prefix");
        locked();api->set_caret_pos(session,2);key(0xff08);
        check(input()=="acd" && api->get_caret_pos(session)==1 && committed.empty(),"Real locked backspace ignored caret");
        check(property("tiger_sentence_locks").empty(),"Real backspace retained invalid lock");
        // locked() confirms only "ab", not the subsequently typed "cd".
        // Delete inside "ab" must unlock; deleting in its suffix must preserve it.
        locked();api->set_caret_pos(session,1);key(0xffff);
        check(input()=="acd" && api->get_caret_pos(session)==1 && committed.empty(),"Real locked Delete ignored caret");
        check(property("tiger_sentence_locks").empty(),"Real Delete retained intersected lock");
        locked();const auto prefixLock=property("tiger_sentence_locks");
        api->set_caret_pos(session,2);key(0xffff);
        check(input()=="abd" && api->get_caret_pos(session)==2 && committed.empty(),"Real suffix Delete ignored caret");
        check(property("tiger_sentence_locks")==prefixLock,"Real suffix Delete discarded unaffected lock");
        reset(false);type("ja");check(selection()==0,"Unexpected initial selection");
        for(int i=1;i<=20;++i){key(0xff09);check(selection()==i%20,"Real Tab wrapped at prepared page instead of true end");check(committed.empty(),"Tab committed a candidate");}
        key(0xff09,1);check(selection()==19 && committed.empty(),"Real Shift+Tab missed last candidate");
        // Fault injection is supplied only by the test copy's rime.lua adapter.
        reset(true);api->set_option(session,"review_model_failure",True);type("ab");
        check(input()=="ab" && committed.empty(),"Real model failure leaked/duplicated the key");
        check(property("review_model_failed")=="yes","Injected model failure was not reached");
        reset(false);key('1');key('.');check(committed=="1.","Idle digit/decimal behavior changed");
        std::cout<<"{\"status\":\"passed\",\"checks\":"<<checks<<",\"librime\":\""<<api->get_version()
            <<"\",\"real_processor_chain\":true,\"synthetic_code_table\":true,\"physical_input\":false}\n";
        api->destroy_session(session);api->finalize();return 0;
    }catch(const std::exception&e){std::cerr<<e.what()<<" input="<<(session?input():"")<<" committed="<<committed<<'\n';if(session)api->destroy_session(session);if(api)api->finalize();return 1;}
}
