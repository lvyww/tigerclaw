#include "lm/model.hh"
#include <vector>
#include <sstream>
#include <iostream>
#include <iomanip>
int main(int argc,char**argv){
 lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;
 auto*m=lm::ngram::LoadVirtual(argv[1],c);std::string line;
 std::cout<<std::setprecision(12);
 while(std::getline(std::cin,line)){
  std::vector<char>a(m->StateSize()),b(m->StateSize());m->BeginSentenceWrite(a.data());
  std::istringstream in(line);std::string token;std::vector<lm::WordIndex> history{m->BaseVocabulary().BeginSentence()};
  double full=0,truncated=0;unsigned matches[7]={};
  std::vector<lm::WordIndex>ids;while(in>>token)ids.push_back(m->BaseVocabulary().Index(token));ids.push_back(m->BaseVocabulary().EndSentence());
  for(auto id:ids){auto f=m->BaseFullScore(a.data(),id,b.data());full+=f.prob;matches[f.ngram_length]++;a.swap(b);
   std::vector<lm::WordIndex>rev(history.rbegin(),history.rend());if(rev.size()>2)rev.resize(2);
   truncated+=m->BaseFullScoreForgotState(rev.data(),rev.data()+rev.size(),id,b.data()).prob;history.push_back(id);
  }
  std::cout<<full<<'\t'<<truncated;for(int i=1;i<=5;i++)std::cout<<'\t'<<matches[i];std::cout<<'\n';
 }
 delete m;
}
