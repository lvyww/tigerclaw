#include "lm/model.hh"
#include "lm/state.hh"
#include <limits>
#include <string>
static thread_local std::string error;
extern "C" {
const char* joint_error(){return error.c_str();}
void* joint_load(const char* path){try{
  lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;
  auto* m=lm::ngram::LoadVirtual(path,c);
  if(m->Order()!=3){delete m;throw std::runtime_error("Expected a trigram model");}
  return m;
}catch(const std::exception&e){error=e.what();return nullptr;}}
void joint_free(void* p){delete static_cast<lm::base::Model*>(p);}
unsigned joint_index(void* p,const char* token){
  return static_cast<lm::base::Model*>(p)->BaseVocabulary().Index(token);
}
double joint_score(void* p,unsigned a,unsigned b,unsigned target){try{
  auto* m=static_cast<lm::base::Model*>(p);lm::ngram::State next;
  if(b==m->BaseVocabulary().BeginSentence())
    return m->BaseScore(m->BeginSentenceMemory(),target,&next);
  lm::WordIndex history[]={b,a};
  return m->BaseFullScoreForgotState(history,history+2,target,&next).prob;
}catch(const std::exception&e){error=e.what();return std::numeric_limits<double>::quiet_NaN();}}
}
