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
 if(argc!=6)throw std::runtime_error("input.arpa output1.arpa output2.arpa thresholds2:3:4:5,thresholds2:3:4:5");
 std::array<std::array<int,6>,2> limit{};
 std::istringstream specs(argv[4]);std::string spec;
 for(int j=0;j<2;j++){
  if(!std::getline(specs,spec,','))throw std::runtime_error("need two threshold sets");
  std::istringstream parts(spec);std::string part;
  for(int n=2;n<=5;n++){if(!std::getline(parts,part,':'))throw std::runtime_error("need 2:3:4:5 thresholds");limit[j][n]=std::stoi(part);if(limit[j][n]<0 || (n>2&&limit[j][n]>limit[j][n-1]))throw std::runtime_error("thresholds must be nonnegative and nonincreasing");}
 }
 if(std::getline(specs,spec,','))throw std::runtime_error("extra thresholds");
 std::vector<char>readBuffer(8*1024*1024);std::ifstream in;in.rdbuf()->pubsetbuf(readBuffer.data(),readBuffer.size());in.open(argv[1]);if(!in)throw std::runtime_error("input");
 std::string line;bool uni=false;std::vector<std::pair<double,std::string>>v;
 while(std::getline(in,line)){if(line=="\\1-grams:"){uni=true;continue;}if(line=="\\2-grams:")break;if(!uni||line.empty())continue;auto f=fields(line);if(f.size()<2)continue;std::string t(f[1]);if(t=="<s>"||t=="</s>"||t=="<unk>")continue;v.emplace_back(std::stod(std::string(f[0])),t);}
 std::unordered_map<std::string,int,Hash,std::equal_to<>> ranks;
 std::ifstream rankFile(argv[5]);std::string token;int rank;
 while(rankFile>>token>>rank)ranks[token]=rank;
 if(ranks.empty())throw std::runtime_error("empty unified rank table");
 ranks["<s>"]=ranks["</s>"]=ranks["<unk>"]=0;
 in.clear();in.seekg(0);std::array<std::ofstream,2>out;for(int j=0;j<2;j++){out[j].open(argv[2+j],std::ios::binary);if(!out[j])throw std::runtime_error("output");out[j]<<std::string(511,' ')<<'\n';}
 std::array<unsigned long long,6> expected{},observed{};std::array<std::array<unsigned long long,6>,2>counts{};int n=0;unsigned long long total=0;
 while(std::getline(in,line)){
  if(line.rfind("ngram ",0)==0){auto eq=line.find('=');expected[std::stoi(line.substr(6,eq-6))]=std::stoull(line.substr(eq+1));continue;}
  if(line.size()>3&&line[0]=='\\'&&line.find("-grams:")!=std::string::npos){n=std::stoi(line.substr(1));if(n>5)throw std::runtime_error("order");for(auto&o:out)o<<'\n'<<line<<'\n';continue;}
  if(!n||line.empty()||line[0]=='\\')continue;
  observed[n]++;auto f=fields(line);if(f.size()!=size_t(n+1)&&f.size()!=size_t(n+2))throw std::runtime_error("bad ARPA record");
  int historyMax=0,fullMax=0;
  if(n>=1)for(int k=1;k<=n;k++){auto it=ranks.find(f[k]);if(it==ranks.end())throw std::runtime_error("unknown token");fullMax=std::max(fullMax,it->second);if(k<n)historyMax=fullMax;}
  for(int j=0;j<2;j++){
   bool keep=n<=1||historyMax<=limit[j][n];if(!keep)continue;
   counts[j][n]++;
   if((n>=1&&n<5)&&fullMax>limit[j][n+1]){
    // Preserve the conditional probability; replace only this context's backoff.
    out[j]<<f[0]<<'\t';for(int k=1;k<=n;k++){if(k>1)out[j]<<' ';out[j]<<f[k];}out[j]<<"\t0\n";
   }else out[j]<<line<<'\n';
  }
  if(++total%10000000==0)std::cerr<<"records "<<total<<" order "<<n<<'\n';
 }
 if(!in.eof()||expected!=observed||n!=5){std::cerr<<"read flags "<<in.rdstate()<<" eof "<<in.eof()<<" errno "<<errno<<"\n";throw std::runtime_error("incomplete ARPA input; refuse partial model");}
 for(int j=0;j<2;j++){out[j]<<"\n\\end\\\n";out[j].seekp(0);std::ostringstream h;h<<"\\data\\\n";for(int k=1;k<=5;k++)h<<"ngram "<<k<<'='<<counts[j][k]<<'\n';auto s=h.str();if(s.size()>511)throw std::runtime_error("header");out[j]<<s<<std::string(511-s.size(),' ')<<'\n';out[j].flush();if(!out[j])throw std::runtime_error("write failed");std::cerr<<"limit "<<limit[j][2]<<":"<<limit[j][3]<<":"<<limit[j][4]<<":"<<limit[j][5];for(int k=1;k<=5;k++)std::cerr<<' '<<counts[j][k];std::cerr<<'\n';}
}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
