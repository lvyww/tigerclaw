#include "lm/model.hh"
#include <vector>
#include <string>
#include <limits>
static thread_local std::string error;
extern "C" {
const char* joint_error(){return error.c_str();}
void* joint_load(const char*p){try{lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;auto*m=lm::ngram::LoadVirtual(p,c);if(m->Order()<3||m->Order()>6){delete m;throw std::runtime_error("Unsupported order");}return m;}catch(const std::exception&e){error=e.what();return nullptr;}}
void joint_free(void*p){delete static_cast<lm::base::Model*>(p);}
int joint_order(void*p){return static_cast<lm::base::Model*>(p)->Order();}
unsigned joint_index(void*p,const char*t){return static_cast<lm::base::Model*>(p)->BaseVocabulary().Index(t);}
double joint_history_score(void*p,const unsigned*h,int n,unsigned t){try{auto*m=static_cast<lm::base::Model*>(p);std::vector<char>next(m->StateSize());if(n==1&&h[0]==m->BaseVocabulary().BeginSentence())return m->BaseScore(m->BeginSentenceMemory(),t,next.data());return m->BaseFullScoreForgotState(h,h+n,t,next.data()).prob;}catch(const std::exception&e){error=e.what();return std::numeric_limits<double>::quiet_NaN();}}
}
