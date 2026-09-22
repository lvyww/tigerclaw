// Offline full-context scoring. Model owns its state layout; no trigram truncation.
#include "lm/model.hh"
#include <vector>
#include <sstream>
#include <string>
#include <limits>
static thread_local std::string error;
extern "C" {
const char* ho_error(){return error.c_str();}
void* ho_load(const char* path){try{lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;return lm::ngram::LoadVirtual(path,c);}catch(const std::exception&e){error=e.what();return nullptr;}}
void ho_free(void*p){delete static_cast<lm::base::Model*>(p);}
unsigned ho_order(void*p){return static_cast<lm::base::Model*>(p)->Order();}
double ho_score(void*p,const char* text,unsigned* oov){try{
 auto*m=static_cast<lm::base::Model*>(p);std::vector<char>a(m->StateSize()),b(m->StateSize());m->BeginSentenceWrite(a.data());
 std::istringstream input(text);std::string token;double score=0;*oov=0;
 while(input>>token){auto id=m->BaseVocabulary().Index(token);*oov+=id==m->BaseVocabulary().NotFound();score+=m->BaseScore(a.data(),id,b.data());a.swap(b);}
 score+=m->BaseScore(a.data(),m->BaseVocabulary().EndSentence(),b.data());return score;
}catch(const std::exception&e){error=e.what();return std::numeric_limits<double>::quiet_NaN();}}
}
