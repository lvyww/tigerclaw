// Lossless ARPA record reordering for IRSTLM's forward-prefix-contiguous loader.
#include <array>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
int main(int argc,char**argv){try{
 if(argc!=5)throw std::runtime_error("split|restore input output counts");
 std::ifstream in(argv[2]);std::ofstream out(argv[3]);
 if(!in||!out)throw std::runtime_error("open");
 std::array<unsigned long long,7> expected{},actual{};std::string s;int n=0;bool ended=false;
 if(std::string(argv[1])=="split"){
  while(std::getline(in,s)){
   if(s.empty())continue;
   if(s.starts_with("ngram ")){auto eq=s.find('=');expected.at(std::stoi(s.substr(6)))=std::stoull(s.substr(eq+1));continue;}
   if(s[0]=='\\'){n=s.find("-grams:")!=std::string::npos?std::stoi(s.substr(1)):0;ended|=s=="\\end\\";continue;}
   if(!n)continue;
   auto a=s.find('\t');auto b=s.find('\t',a+1);
   if(a==std::string::npos)throw std::runtime_error("expected tab ARPA");
   auto tokens=s.substr(a+1,b==std::string::npos?b:b-a-1);
   out<<n<<'\t'<<tokens<<'\t'<<s.substr(0,a);
   if(b!=std::string::npos)out<<s.substr(b);
   out<<'\n';++actual[n];
  }
  if(!ended||actual!=expected)throw std::runtime_error("truncated/count mismatch");
  std::ofstream meta(argv[4]);for(int i=1;i<7;++i)meta<<expected[i]<<'\n';
 }else{
  std::ifstream meta(argv[4]);for(int i=1;i<7;++i)meta>>expected[i];
  out<<"\\data\\\n";for(int i=1;i<7;++i)if(expected[i])out<<"ngram "<<i<<'='<<expected[i]<<'\n';
  std::string previous;
  while(std::getline(in,s)){
   auto a=s.find('\t'),b=s.find('\t',a+1),c=s.find('\t',b+1);
   if(b==std::string::npos)throw std::runtime_error("sorted record");
   int order=std::stoi(s.substr(0,a));auto key=s.substr(0,b);
   if(!previous.empty()&&key<=previous)throw std::runtime_error("duplicate/unsorted key");previous=key;
   if(order!=n){n=order;out<<"\n\\"<<n<<"-grams:\n";}
   out<<s.substr(b+1,c==std::string::npos?c:c-b-1)<<'\t'<<s.substr(a+1,b-a-1);
   if(c!=std::string::npos)out<<s.substr(c);
   out<<'\n';++actual[n];
  }
  if(actual!=expected)throw std::runtime_error("sorted count mismatch");
  out<<"\n\\end\\\n";
 }
 if(in.bad()||!out)throw std::runtime_error("I/O failure");
 for(int i=1;i<7;++i)std::cerr<<i<<":"<<actual[i]<<" ";std::cerr<<"\n";
}catch(const std::exception&e){std::cerr<<e.what()<<"\n";return 1;}}
