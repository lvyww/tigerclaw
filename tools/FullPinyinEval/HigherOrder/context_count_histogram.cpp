// Vocabulary-conditioned CONTEXT pruning, not count/entropy pruning.
// Keep all unigram probabilities; order-specific suffix-closed thresholds. Enable higher-order distributions only when every
// token in their history belongs to a fixed top-unigram vocabulary.
// Dropped complete distributions use backoff 1. The retained-context policy
// is suffix-closed, so retained distributions' lower-order probabilities
// are unchanged and their original normalization remains valid.
#include <fstream>
#include <iostream>
#include <sstream>
#include <vector>
#include <unordered_map>
#include <algorithm>
#include <string_view>
#include <array>
#include <stdexcept>
struct Hash {using is_transparent=void;size_t operator()(std::string_view v)const{return std::hash<std::string_view>{}(v);}};
std::vector<std::string_view> fields(const std::string&s){std::vector<std::string_view>v;size_t i=0;while(i<s.size()){while(i<s.size()&&isspace((unsigned char)s[i]))i++;size_t b=i;while(i<s.size()&&!isspace((unsigned char)s[i]))i++;if(i>b)v.emplace_back(s.data()+b,i-b);}return v;}
int main(int argc,char**argv){try{
 if(argc!=2)throw std::runtime_error("input.arpa");
 std::vector<char>readBuffer(8*1024*1024);std::ifstream in;in.rdbuf()->pubsetbuf(readBuffer.data(),readBuffer.size());in.open(argv[1]);if(!in)throw std::runtime_error("input");
 std::string line;bool uni=false;std::vector<std::pair<double,std::string>>v;
 while(std::getline(in,line)){if(line=="\\1-grams:"){uni=true;continue;}if(line=="\\2-grams:")break;if(!uni||line.empty())continue;auto f=fields(line);if(f.size()<2)continue;std::string t(f[1]);if(t=="<s>"||t=="</s>"||t=="<unk>")continue;v.emplace_back(std::stod(std::string(f[0])),t);}
 std::sort(v.begin(),v.end(),[](auto&a,auto&b){return a.first!=b.first?a.first>b.first:a.second<b.second;});
 std::unordered_map<std::string,int,Hash,std::equal_to<>> ranks;ranks.reserve(v.size()*2);for(size_t i=0;i<v.size();i++)ranks[v[i].second]=i+1;ranks["<s>"]=ranks["</s>"]=ranks["<unk>"]=0;

 in.clear();in.seekg(0);int n=0;std::array<unsigned long long,6>expected{},observed{};
 std::array<std::vector<unsigned long long>,6>hist;for(auto&h:hist)h.resize(v.size()+1);
 while(std::getline(in,line)){
  if(line.rfind("ngram ",0)==0){auto eq=line.find('=');expected[std::stoi(line.substr(6,eq-6))]=std::stoull(line.substr(eq+1));continue;}
  if(line.size()>3&&line[0]=='\\'&&line.find("-grams:")!=std::string::npos){n=std::stoi(line.substr(1));if(n>5)throw std::runtime_error("order");continue;}
  if(!n||line.empty()||line[0]=='\\')continue;
  auto f=fields(line);if(f.size()!=size_t(n+1)&&f.size()!=size_t(n+2))throw std::runtime_error("record");
  observed[n]++;int maximum=0;
  for(int k=1;k<n;k++){auto it=ranks.find(f[k]);if(it==ranks.end())throw std::runtime_error("token");maximum=std::max(maximum,it->second);}
  hist[n][maximum]++;
 }
 if(!in.eof()||expected!=observed||n!=5)throw std::runtime_error("incomplete input");
 std::cout<<"rank\torder2\torder3\torder4\torder5\n";
 std::array<unsigned long long,6>sum{};
 for(size_t k=0;k<=v.size();k++){std::cout<<k;for(int j=2;j<=5;j++){sum[j]+=hist[j][k];std::cout<<'\t'<<sum[j];}std::cout<<'\n';}
}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
