#include "mixture_budget.hpp"
#include <charconv>
#include <parallel/algorithm>
#include <omp.h>
#include <queue>
#include <sstream>
#include <string_view>
using namespace mb;
std::vector<std::string> fields(const std::string&s){std::vector<std::string> v;size_t i=0;while(i<s.size()){while(i<s.size()&&isspace((unsigned char)s[i]))++i;size_t j=i;while(i<s.size()&&!isspace((unsigned char)s[i]))++i;if(i>j)v.push_back(s.substr(j,i-j));}return v;}
std::vector<std::string> unigrams(const fs::path&p){std::ifstream f(p);require(bool(f),"ARPA open");std::string s;bool active=false;std::vector<std::string> v;
 while(std::getline(f,s)){if(s=="\\2-grams:")break;if(s=="\\1-grams:"){active=true;continue;}if(active&&!s.empty()&&s[0]!='\\'){auto x=fields(s);require(x.size()>=2,"unigram parse");v.push_back(x[1]);}}return v;}
struct Sorter {
 fs::path dir;int order;size_t capacity;std::vector<Record> buffer;std::vector<fs::path> runs;size_t total=0;
 Sorter(fs::path d,int n,size_t cap):dir(d),order(n),capacity(cap){buffer.reserve(cap);}
 void flush(){if(buffer.empty())return;__gnu_parallel::sort(buffer.begin(),buffer.end(),less);auto p=dir/("sort-"+std::to_string(order)+"-"+std::to_string(runs.size())+".tmp");save_records(p,buffer.data(),buffer.size());runs.push_back(p);total+=buffer.size();buffer.clear();}
 void put(Record r){buffer.push_back(r);if(buffer.size()==capacity)flush();}
 void finish(){flush();auto dest=dir/(std::to_string(order)+".bin");
  if(runs.size()==1){fs::rename(runs[0],dest);return;}
  struct Cursor{Record r;size_t i;};auto cmp=[](const Cursor&a,const Cursor&b){return less(b.r,a.r);};std::priority_queue<Cursor,std::vector<Cursor>,decltype(cmp)> heap(cmp);
  std::vector<std::ifstream> inputs;for(auto&p:runs)inputs.emplace_back(p,std::ios::binary);
  for(size_t i=0;i<inputs.size();++i){Record r;if(inputs[i].read(reinterpret_cast<char*>(&r),sizeof r))heap.push({r,i});}
  std::ofstream out(dest,std::ios::binary);Record previous{};bool first=true;size_t count=0;
  while(!heap.empty()){auto c=heap.top();heap.pop();require(first||less(previous,c.r),"duplicate record");previous=c.r;first=false;out.write(reinterpret_cast<char*>(&c.r),sizeof c.r);++count;
   if(inputs[c.i].read(reinterpret_cast<char*>(&c.r),sizeof c.r))heap.push(c);else require(inputs[c.i].eof(),"merge read");}
  out.close();require(bool(out)&&count==total,"sort output mismatch");for(auto&p:runs)fs::remove(p);
 }
};
int main(int argc,char**argv){try{
 require(argc>=2,"vocab A.arpa B.arpa output | pack vocab arpa output threads block_records");
 if(std::string(argv[1])=="vocab"){
  require(argc==5,"vocab arguments");auto a=unigrams(argv[2]),b=unigrams(argv[3]);a.insert(a.end(),b.begin(),b.end());std::sort(a.begin(),a.end());a.erase(std::unique(a.begin(),a.end()),a.end());std::ofstream f(argv[4]);for(auto&s:a)f<<s<<'\n';require(bool(f),"vocabulary output");std::cout<<"union_vocab "<<a.size()<<'\n';return 0;
 }
 require(argc==7&&std::string(argv[1])=="pack","pack arguments");Vocabulary v(argv[2]);fs::path dir=argv[4];fs::create_directories(dir);if(fs::exists(dir/"complete.txt")){std::cout<<"already packed\n";return 0;}fs::copy_file(argv[2],dir/"vocab.txt",fs::copy_options::overwrite_existing);omp_set_num_threads(std::stoi(argv[5]));size_t block=std::stoull(argv[6]);
 std::vector<char> io_buffer(32*1024*1024);std::ifstream f;f.rdbuf()->pubsetbuf(io_buffer.data(),io_buffer.size());f.open(argv[3]);require(bool(f),"input open");std::string s;int n=0;std::array<uint64_t,6> expected{},actual{};std::unique_ptr<Sorter> sorter;bool ended=false,resume=false;
 while(std::getline(f,s)){
  if(s.empty())continue;if(s.starts_with("ngram ")){int k=std::stoi(s.substr(6));require(k>=1&&k<=5,"order");expected[k]=std::stoull(s.substr(s.find('=')+1));continue;}
  if(s[0]=='\\'){
   if(s.find("-grams:")!=std::string::npos){if(sorter){sorter->finish();sorter.reset();std::cout<<"complete order "<<n<<" records "<<actual[n]<<std::endl;}n=std::stoi(s.substr(1));require(n>=1&&n<=5,"section");auto p=dir/(std::to_string(n)+".bin");resume=fs::exists(p)&&fs::file_size(p)==expected[n]*sizeof(Record);if(resume)std::cout<<"reuse complete order "<<n<<std::endl;else sorter=std::make_unique<Sorter>(dir,n,block);}
   else if(s=="\\end\\"){ended=true;break;}continue;
  }
  if(!n)continue;++actual[n];if(resume)continue;
  std::array<std::string_view,8> x;size_t count=0,i=0;while(i<s.size()){while(i<s.size()&&isspace((unsigned char)s[i]))++i;size_t start=i;while(i<s.size()&&!isspace((unsigned char)s[i]))++i;if(i>start){require(count<x.size(),"too many fields");x[count++]=std::string_view(s).substr(start,i-start);}}
  require(count==size_t(n+1)||count==size_t(n+2),"ARPA fields");Record r{};
  auto number=[](std::string_view text){float value;auto result=std::from_chars(text.data(),text.data()+text.size(),value);require(result.ec==std::errc()&&result.ptr==text.data()+text.size()&&std::isfinite(value),"finite source number");return value;};
  r.p=number(x[0]);if(count==size_t(n+2))r.b=number(x[count-1]);
  for(int k=0;k<n;++k)r.w[k]=v.ids.at(std::string(x[k+1]));sorter->put(r);if(actual[n]%10000000==0)std::cout<<"parsed "<<n<<' '<<actual[n]<<std::endl;
 }
 require(ended&&actual==expected,"ARPA counts/end mismatch");if(sorter)sorter->finish();
 for(int k=1;k<=5;++k)if(!fs::exists(dir/(std::to_string(k)+".bin")))save_records(dir/(std::to_string(k)+".bin"),nullptr,0);
 std::ofstream done(dir/"complete.txt");for(int k=1;k<=5;++k)done<<actual[k]<<'\n';std::cout<<"PACK COMPLETE\n";
}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
