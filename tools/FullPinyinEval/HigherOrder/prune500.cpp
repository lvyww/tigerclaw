// IRSTLM weighted-difference pruning, with cross-tool score guard before mutation.
#include "lmtable.h"
#include <cmath>
#include <fstream>
#include <iomanip>
#include <sstream>
#include <stdexcept>
#include <string>
#include <iostream>
using namespace irstlm;
int main(int argc,char**argv){try{
 if(argc!=6)throw std::runtime_error("input output threshold score-probes checkpoint-or-dash");
 lmtable model;inputfilestream input(argv[1]);if(!input.good())throw std::runtime_error("input");
 model.load(input,argv[1],argv[2],0);model.getDict()->incflag(0);
 const unsigned expected[]={0,20934,7528633,65305265,183986976,265214769};
 if(model.maxlevel()!=5)throw std::runtime_error("order");
 for(int i=1;i<=5;++i)if(model.getCurrentSize(i)!=expected[i])throw std::runtime_error("loaded count differs from original");
 std::ifstream probes(argv[4]);std::string line;double maxdelta=0;int count=0;
 while(std::getline(probes,line)){
  std::istringstream row(line);double expectedScore;row>>expectedScore;std::string token;
  ngram ng(model.getDict(),0);ng.pushc(model.getDict()->encode("<s>"));double score=0;
  while(row>>token){ng.pushc(model.getDict()->encode(token.c_str()));if(ng.size>5)ng.size=5;score+=model.lprob(ng);}
  ng.pushc(model.getDict()->encode("</s>"));if(ng.size>5)ng.size=5;score+=model.lprob(ng);
  maxdelta=std::max(maxdelta,std::abs(score-expectedScore));
  if(!std::isfinite(score)||std::abs(score-expectedScore)>0.002)throw std::runtime_error("pre-prune score mismatch: "+std::to_string(score)+" vs "+std::to_string(expectedScore));
  ++count;
 }
 if(count!=5000)throw std::runtime_error("probe count");
 std::cerr<<"VERIFIED pre-prune native KenLM equivalence: "<<count<<" paths max log10 delta="<<std::setprecision(12)<<maxdelta<<std::endl;
 if(std::string(argv[5])!="-")model.savebin(argv[5]);
 float thresholds[MAX_NGRAM]={};std::istringstream spec(argv[3]);std::string part;int index=1;
 while(std::getline(spec,part,',')){if(index>4)throw std::runtime_error("too many thresholds");thresholds[index++]=std::stof(part);}
 if(index!=2&&index!=5)throw std::runtime_error("one shared or four order-specific thresholds required");
 for(int i=index;i<MAX_NGRAM;++i)thresholds[i]=thresholds[index-1];
 model.wdprune(thresholds,1);model.savetxt(argv[2]);
 std::cerr<<"PRUNE COMPLETE threshold="<<argv[3]<<std::endl;
}catch(const std::exception&e){std::cerr<<e.what()<<std::endl;return 1;}}
