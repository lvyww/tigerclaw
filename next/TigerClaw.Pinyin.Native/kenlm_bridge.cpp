#include "lm/model.hh"
#include "lm/state.hh"
#include <limits>
#include <string>
#include <sstream>
#include <vector>
#include <memory>
static thread_local std::string error;
struct JointContexts {
  lm::base::Model* model;
  std::vector<lm::ngram::State> states;
};
extern "C" {
void* joint_contexts_create(void* p,const unsigned* pairs,unsigned count){try{
  auto* m=static_cast<lm::base::Model*>(p);
  auto query=std::unique_ptr<JointContexts>(new JointContexts{m,{}});query->states.resize(count);
  for(unsigned i=0;i<count;++i){
    unsigned a=pairs[2*i],b=pairs[2*i+1];
    if(b==m->BaseVocabulary().BeginSentence())m->BeginSentenceWrite(&query->states[i]);
    else m->BaseFullScoreForgotState(&a,&a+1,b,&query->states[i]);
  }
  return query.release();
}catch(const std::exception&e){error=e.what();return nullptr;}}
void joint_contexts_free(void* p){delete static_cast<JointContexts*>(p);}
int joint_contexts_score(void* p,unsigned target,double* output){try{
  auto* q=static_cast<JointContexts*>(p);lm::ngram::State next;
  for(size_t i=0;i<q->states.size();++i)output[i]=q->model->BaseScore(&q->states[i],target,&next);
  return 1;
}catch(const std::exception&e){error=e.what();return 0;}}

const char* joint_error(){return error.c_str();}
void* joint_load(const char* path){try{
  lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;
  auto* m=lm::ngram::LoadVirtual(path,c);
  if(m->Order()!=3){delete m;throw std::runtime_error("Expected a trigram model");}
  return m;
}catch(const std::exception&e){error=e.what();return nullptr;}}
void joint_free(void* p){delete static_cast<lm::base::Model*>(p);}
// Separate entry point: never silently truncate a five-gram to trigram history.
void* joint_load_fivegram(const char* path){try{
  lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;
  auto* m=lm::ngram::LoadVirtual(path,c);
  if(m->Order()!=5){delete m;throw std::runtime_error("Expected a five-gram model");}
  return m;
}catch(const std::exception&e){error=e.what();return nullptr;}}
double joint_sentence_score(void* p,const char* tokens){try{
  auto* m=static_cast<lm::base::Model*>(p);
  std::vector<char> a(m->StateSize()),b(m->StateSize());
  m->BeginSentenceWrite(a.data());
  std::istringstream input(tokens);std::string token;double score=0;
  while(input>>token){
    score+=m->BaseScore(a.data(),m->BaseVocabulary().Index(token),b.data());
    a.swap(b);
  }
  return score+m->BaseScore(a.data(),m->BaseVocabulary().EndSentence(),b.data());
}catch(const std::exception&e){error=e.what();return std::numeric_limits<double>::quiet_NaN();}}
unsigned joint_index(void* p,const char* token){
  return static_cast<lm::base::Model*>(p)->BaseVocabulary().Index(token);
}
// Explicit full-history ABI for shape-code fivegram search. Existing pinyin ABI
// remains unchanged. Histories are newest first and contain at most one BOS.
double joint_score_history4(void* p,unsigned a,unsigned b,unsigned c,unsigned d,unsigned count,unsigned target){try{
  auto* m=static_cast<lm::base::Model*>(p);
  if(m->Order()!=5 || count>4)throw std::runtime_error("Expected fivegram history of at most four tokens");
  lm::WordIndex history[]={a,b,c,d};lm::ngram::State next;
  return m->BaseFullScoreForgotState(history,history+count,target,&next).prob;
}catch(const std::exception&e){error=e.what();return std::numeric_limits<double>::quiet_NaN();}}
double joint_score(void* p,unsigned a,unsigned b,unsigned target){try{
  auto* m=static_cast<lm::base::Model*>(p);lm::ngram::State next;
  if(b==m->BaseVocabulary().BeginSentence())
    return m->BaseScore(m->BeginSentenceMemory(),target,&next);
  lm::WordIndex history[]={b,a};
  return m->BaseFullScoreForgotState(history,history+2,target,&next).prob;
}catch(const std::exception&e){error=e.what();return std::numeric_limits<double>::quiet_NaN();}}
}
