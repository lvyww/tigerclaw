#include "mixture_budget.hpp"
#include <iomanip>
#include <omp.h>
#include <charconv>
using namespace mb;
static void copy_vocab(const fs::path&from,const fs::path&to){fs::create_directories(to);fs::copy_file(from/"vocab.txt",to/"vocab.txt",fs::copy_options::overwrite_existing);}
// Exact single-deletion KL for a normalized backoff context, used as an
// independent ranking estimate. Simultaneous removals are NOT additive/exact.
static double deletion_loss(double p,double q,double r,double t){
 if(p==0)return 0;if(q==0)return std::numeric_limits<double>::infinity();
 double log_nb=std::log(r+p)-std::log(t+q);
 double d=p*(std::log(p)-std::log(q)-log_nb);
 if(r>0&&t>0)d+=r*(std::log(r)-std::log(t)-log_nb);
 require(std::isfinite(d),"nonfinite deletion loss");return std::max(0.,d);
}
static void materialize(const fs::path&ap,const fs::path&bp,const fs::path&out,double alpha,int threads,int start=2,int stop=5){
 require(alpha>0&&alpha<1&&threads>=1&&threads<=12,"mixture arguments");Model a(ap),b(bp);require(a.vocab.words==b.vocab.words,"vocabulary mismatch");copy_vocab(ap,out);
 require(start>=2&&start<=stop&&stop<=5,"resume order");
 if(start==2){std::vector<Record> uni;double mass=0;
 for(size_t w=0;w<a.vocab.words.size();++w){Record r;r.w[0]=w;r.p=mix_log(a.extended_score(r.w,1),b.extended_score(r.w,1),alpha);if(w!=a.vocab.bos)mass+=std::pow(10.,r.p);uni.push_back(r);}
 require(std::abs(mass-1)<2e-5,"source unigram normalization");
 for(auto&r:uni)r.p=r.w[0]==a.vocab.bos?0:r.p-std::log10(mass);
 save_records(out/"1.bin",uni.data(),uni.size());}
 else require(fs::exists(out/(std::to_string(start-1)+".ready")),"resume checkpoint missing");
 Model full(out,true);omp_set_num_threads(threads);
 for(int n=start;n<=stop;++n){
  std::vector<int> boundaries{0};auto& aa=*a.sections[n];
  for(int k=1;k<threads;++k)if(aa.size){int w=aa.data[aa.size*k/threads].w[0];if(w>boundaries.back())boundaries.push_back(w);}
  boundaries.push_back(a.vocab.words.size());int partitions=boundaries.size()-1;std::string failure;std::vector<uint64_t> counts(partitions);
  #pragma omp parallel for schedule(dynamic,1)
  for(int part=0;part<partitions;++part){try{
   int lo=boundaries[part],hi=boundaries[part+1];size_t i=a.starts[n][lo],ie=a.starts[n][hi],j=b.starts[n][lo],je=b.starts[n][hi];
   auto path=out/(std::to_string(n)+"-part-"+std::to_string(part)+".tmp");std::ofstream f(path,std::ios::binary);require(bool(f),"part output");
   std::vector<Record> group;std::vector<double> lower;
   auto emit=[&](){if(group.empty())return;double sum=0,qsum=0;lower.clear();lower.reserve(group.size());
    for(auto&r:group){sum+=std::pow(10.,r.p);double q=std::pow(10.,full.score(r.w+1,n-1));lower.push_back(q);qsum+=q;}
    require(sum<=1+2e-5,"merged support mass exceeds one");
    if(sum>1){double shift=std::log10(sum);sum=0;for(auto&r:group){r.p-=shift;sum+=std::pow(10.,r.p);}}
    auto h=full.find(group[0].w,n-1);require(h,"missing prefix while materializing");h->b=backoff(sum,qsum);
    double r=std::max(0.,1-sum),t=std::max(0.,1-qsum),history=full.history_log_mass(group[0].w,n-1);
    for(size_t z=0;z<group.size();++z){double p=std::pow(10.,group[z].p),d=deletion_loss(p,lower[z],r,t);group[z].loss=d>0?history+std::log10(d):-std::numeric_limits<float>::infinity();}
    f.write(reinterpret_cast<const char*>(group.data()),group.size()*sizeof(Record));counts[part]+=group.size();group.clear();
   };
   while(i<ie||j<je){Record r;const Record*ar=i<ie?a.sections[n]->data+i:nullptr;const Record*br=j<je?b.sections[n]->data+j:nullptr;
    int c=!ar?1:!br?-1:compare(ar->w,br->w,n);const Record*key=c<=0?ar:br;r=*key;r.b=0;r.loss=-std::numeric_limits<float>::infinity();
    double x=c<=0?ar->p-(r.w[n-1]==a.vocab.unk?std::log10(double(a.unknown_slots)):0):a.extended_score(r.w,n,true);
    double y=c>=0?br->p-(r.w[n-1]==b.vocab.unk?std::log10(double(b.unknown_slots)):0):b.extended_score(r.w,n,true);
    r.p=mix_log(x,y,alpha);
    if(!group.empty()&&compare(group[0].w,r.w,n-1)!=0)emit();group.push_back(r);
    if(c<=0)++i;if(c>=0)++j;
   }
   emit();f.close();require(bool(f),"part write");
  }catch(const std::exception&e){
   #pragma omp critical
   {if(failure.empty())failure=e.what();}
  }}
  require(failure.empty(),failure);full.sections[n-1]->sync();auto dest=out/(std::to_string(n)+".bin");std::ofstream f(dest,std::ios::binary);uint64_t total=0;
  for(int part=0;part<partitions;++part){auto p=out/(std::to_string(n)+"-part-"+std::to_string(part)+".tmp");if(counts[part]){std::ifstream in(p,std::ios::binary);f<<in.rdbuf();require(bool(f),"concatenation write");}total+=counts[part];}
  f.close();require(bool(f)&&fs::file_size(dest)==total*sizeof(Record),"concatenation count");
  for(int part=0;part<partitions;++part)fs::remove(out/(std::to_string(n)+"-part-"+std::to_string(part)+".tmp"));
  full.add(n,dest,true);std::ofstream(out/(std::to_string(n)+".ready"))<<total<<'\n';std::cout<<"materialized order "<<n<<" records "<<total<<std::endl;
 }
 if(stop==5){std::ofstream f(out/"complete.txt");for(int n=1;n<=5;++n)f<<full.sections[n]->size<<'\n';}
}
static void materialize3(const fs::path&ap,const fs::path&bp,const fs::path&cp,const fs::path&out,int threads,int start=2,int stop=5){
 require(threads>=1&&threads<=12,"mixture arguments");Model a(ap),b(bp),c(cp);require(a.vocab.words==b.vocab.words&&a.vocab.words==c.vocab.words,"vocabulary mismatch");copy_vocab(ap,out);
 require(start>=2&&start<=stop&&stop<=5,"resume order");
 if(start==2){std::vector<Record> uni;double mass=0;
 for(size_t w=0;w<a.vocab.words.size();++w){Record r;r.w[0]=w;r.p=mix_three(a.extended_score(r.w,1),b.extended_score(r.w,1),c.extended_score(r.w,1));if(w!=a.vocab.bos)mass+=std::pow(10.,r.p);uni.push_back(r);}
 require(std::abs(mass-1)<2e-5,"source unigram normalization");
 for(auto&r:uni)r.p=r.w[0]==a.vocab.bos?0:r.p-std::log10(mass);
 save_records(out/"1.bin",uni.data(),uni.size());}
 else require(fs::exists(out/(std::to_string(start-1)+".ready")),"resume checkpoint missing");
 Model full(out,true);omp_set_num_threads(threads);
 for(int n=start;n<=stop;++n){

  std::vector<int> boundaries{0};auto& aa=*a.sections[n];
  for(int k=1;k<threads;++k)if(aa.size){int w=aa.data[aa.size*k/threads].w[0];if(w>boundaries.back())boundaries.push_back(w);}
  boundaries.push_back(a.vocab.words.size());int partitions=boundaries.size()-1;std::string failure;std::vector<uint64_t> counts(partitions);
  #pragma omp parallel for schedule(dynamic,1)
  for(int part=0;part<partitions;++part){try{
   int lo=boundaries[part],hi=boundaries[part+1];size_t i=a.starts[n][lo],ie=a.starts[n][hi],j=b.starts[n][lo],je=b.starts[n][hi],k=c.starts[n][lo],ke=c.starts[n][hi];
   auto path=out/(std::to_string(n)+"-part-"+std::to_string(part)+".tmp");std::ofstream f(path,std::ios::binary);require(bool(f),"part output");
   std::vector<Record> group;std::vector<double> lower;
   // Contexts arrive in numeric-key order and full has prefix closure. Walk
   // each prefix section once instead of repeatedly binary-searching it or
   // looking its already observed probability up in a reverse-order trie.
   std::array<size_t,5> prefix_positions{};for(int k=1;k<n;++k)prefix_positions[k]=full.starts[k][lo];
   auto prefix=[&](const uint16_t* key,int k){auto&m=*full.sections[k];auto&pos=prefix_positions[k];
    while(pos<m.size&&compare(m.data[pos].w,key,k)<0)++pos;
    require(pos<m.size&&compare(m.data[pos].w,key,k)==0,"missing sorted history prefix");return m.data+pos;};
   auto emit=[&](){if(group.empty())return;double sum=0,qsum=0;lower.clear();lower.reserve(group.size());
    for(auto&r:group){sum+=std::pow(10.,r.p);double q=std::pow(10.,full.score(r.w+1,n-1));lower.push_back(q);qsum+=q;}
    require(sum<=1+2e-5,"merged support mass exceeds one");
    if(sum>1){double shift=std::log10(sum);sum=0;for(auto&r:group){r.p-=shift;sum+=std::pow(10.,r.p);}}
    auto h=prefix(group[0].w,n-1);h->b=backoff(sum,qsum);
    double r=std::max(0.,1-sum),t=std::max(0.,1-qsum),history=0;
    for(int k=1;k<n;++k)if(!(k==1&&group[0].w[0]==full.vocab.bos))history+=prefix(group[0].w,k)->p;
    for(size_t z=0;z<group.size();++z){double p=std::pow(10.,group[z].p),d=deletion_loss(p,lower[z],r,t);group[z].loss=d>0?history+std::log10(d):-std::numeric_limits<float>::infinity();}
    f.write(reinterpret_cast<const char*>(group.data()),group.size()*sizeof(Record));counts[part]+=group.size();group.clear();
   };
   while(i<ie||j<je||k<ke){
    const Record* records[3]={i<ie?a.sections[n]->data+i:nullptr,j<je?b.sections[n]->data+j:nullptr,k<ke?c.sections[n]->data+k:nullptr};
    const Record* key=nullptr;for(auto q:records)if(q&&(!key||compare(q->w,key->w,n)<0))key=q;
    Record r=*key;r.b=0;r.loss=-std::numeric_limits<float>::infinity();
    Model* models[3]={&a,&b,&c};double scores[3];bool matches[3];
    for(int z=0;z<3;++z){auto&m=*models[z];matches[z]=records[z]&&compare(records[z]->w,key->w,n)==0;
     scores[z]=matches[z]?records[z]->p-(r.w[n-1]==m.vocab.unk?std::log10(double(m.unknown_slots)):0):m.extended_score(r.w,n,true);}
    r.p=mix_three(scores[0],scores[1],scores[2]);
    if(!group.empty()&&compare(group[0].w,r.w,n-1)!=0)emit();group.push_back(r);
    i+=matches[0];j+=matches[1];k+=matches[2];
   }
   emit();f.close();require(bool(f),"part write");
  }catch(const std::exception&e){
   #pragma omp critical
   {if(failure.empty())failure=e.what();}
  }}
  require(failure.empty(),failure);full.sections[n-1]->sync();auto dest=out/(std::to_string(n)+".bin");std::ofstream f(dest,std::ios::binary);uint64_t total=0;
  for(int part=0;part<partitions;++part){auto p=out/(std::to_string(n)+"-part-"+std::to_string(part)+".tmp");if(counts[part]){std::ifstream in(p,std::ios::binary);f<<in.rdbuf();require(bool(f),"concatenation write");}total+=counts[part];}
  f.close();require(bool(f)&&fs::file_size(dest)==total*sizeof(Record),"concatenation count");
  for(int part=0;part<partitions;++part)fs::remove(out/(std::to_string(n)+"-part-"+std::to_string(part)+".tmp"));
  full.add(n,dest,true);std::ofstream(out/(std::to_string(n)+".ready"))<<total<<'\n';std::cout<<"materialized order "<<n<<" records "<<total<<std::endl;
 }
 if(stop==5){std::ofstream f(out/"complete.txt");for(int n=1;n<=5;++n)f<<full.sections[n]->size<<'\n';}
}
static void export_four(const fs::path&input,const fs::path&output,int order=4){
 Vocabulary v(input/"vocab.txt");std::vector<char> buffer(32*1024*1024);std::ofstream f;f.rdbuf()->pubsetbuf(buffer.data(),buffer.size());f.open(output);require(bool(f),"fourgram export open");
 f<<"\\data\\\n";for(int n=1;n<=order;++n)f<<"ngram "<<n<<'='<<fs::file_size(input/(std::to_string(n)+".bin"))/sizeof(Record)<<'\n';
 for(int n=1;n<=order;++n){f<<"\n\\"<<n<<"-grams:\n";Mapping m(input/(std::to_string(n)+".bin"));if(m.size)madvise(m.data,m.size*sizeof(Record),MADV_SEQUENTIAL);
  for(size_t i=0;i<m.size;++i){auto&r=m.data[i];char line[1024];char*p=line;auto number=[&](float x){auto z=std::to_chars(p,line+sizeof(line),x,std::chars_format::general,9);require(z.ec==std::errc(),"float export");p=z.ptr;};number(r.p);*p++='\t';
   for(int k=0;k<n;++k){if(k)*p++=' ';auto&s=v.words[r.w[k]];require(p+s.size()+32<line+sizeof(line),"token too long");std::memcpy(p,s.data(),s.size());p+=s.size();}
   if(n<order){*p++='\t';number(r.b);}*p++='\n';f.write(line,p-line);
  }
  std::cout<<"exported fourgram prefix order "<<n<<std::endl;
 }
 f<<"\n\\end\\\n";f.close();require(bool(f),"fourgram export write");
}
static uint32_t rank_code(float f){require(!std::isnan(f),"NaN importance");uint32_t u=std::bit_cast<uint32_t>(f);return u&0x80000000u?~u:u^0x80000000u;}
static void prune(const fs::path&input,const fs::path&out,const std::array<uint64_t,6>&budget){
 Model full(input);copy_vocab(input,out);std::unique_ptr<Mapping> higher;
 for(int n=5;n>=2;--n){auto&m=*full.sections[n];std::vector<uint64_t> mandatory((m.size+63)/64);uint64_t protected_count=0;
  auto is_protected=[&](size_t i){return bool(mandatory[i/64]&(uint64_t(1)<<(i%64)));};
  if(higher){uint16_t prev[5]{};bool first=true;for(size_t i=0;i<higher->size;++i){auto&r=higher->data[i];if(!first&&compare(prev,r.w,n)==0)continue;std::copy(r.w,r.w+n,prev);first=false;
    size_t at=full.find_index(r.w,n);require(at<m.size,"missing required prefix");if(!is_protected(at)){mandatory[at/64]|=uint64_t(1)<<(at%64);++protected_count;}}}
  uint64_t wanted=std::min<uint64_t>(m.size,std::max(budget[n],protected_count)),free_wanted=wanted-protected_count;
  uint32_t cut=0;uint64_t tie=0;std::vector<uint64_t> bins(65536);
  if(free_wanted){
   for(size_t i=0;i<m.size;++i)if(!is_protected(i))++bins[rank_code(m.data[i].loss)>>16];
   uint64_t need=free_wanted;int high=65535;for(;high>=0;--high){if(need<=bins[high])break;need-=bins[high];}require(high>=0,"selection high");
   std::fill(bins.begin(),bins.end(),0);for(size_t i=0;i<m.size;++i)if(!is_protected(i)){auto c=rank_code(m.data[i].loss);if((c>>16)==uint32_t(high))++bins[c&65535];}
   int low=65535;for(;low>=0;--low){if(need<=bins[low])break;need-=bins[low];}require(low>=0,"selection low");cut=(uint32_t(high)<<16)|uint32_t(low);tie=need;
  }
  auto path=out/(std::to_string(n)+".bin");std::ofstream f(path,std::ios::binary);uint64_t count=0;
  for(size_t i=0;i<m.size;++i){bool keep=is_protected(i);if(!keep&&free_wanted){auto c=rank_code(m.data[i].loss);if(c>cut)keep=true;else if(c==cut&&tie){keep=true;--tie;}}
   if(keep){auto r=m.data[i];r.b=0;f.write(reinterpret_cast<const char*>(&r),sizeof r);++count;}}
  f.close();require(bool(f)&&count==wanted&&tie==0,"pruned count");higher=std::make_unique<Mapping>(path);
  std::cout<<"pruned order "<<n<<" input "<<m.size<<" budget "<<budget[n]<<" protected "<<protected_count<<" retained "<<count<<std::endl;
 }
 std::vector<Record> uni(full.sections[1]->data,full.sections[1]->data+full.sections[1]->size);for(auto&r:uni)r.b=0;save_records(out/"1.bin",uni.data(),uni.size());
 Model selected(out,true);
 for(int n=2;n<=5;++n){auto&m=*selected.sections[n];size_t i=0;while(i<m.size){size_t end=i;double p=0,q=0;while(end<m.size&&compare(m.data[i].w,m.data[end].w,n-1)==0){auto&r=m.data[end++];p+=std::pow(10.,r.p);q+=std::pow(10.,selected.score(r.w+1,n-1));}
   auto h=selected.find(m.data[i].w,n-1);require(h,"selected prefix missing");h->b=backoff(p,q);i=end;}
  selected.sections[n-1]->sync();std::cout<<"recomputed backoff order "<<n-1<<std::endl;
 }
 std::ofstream done(out/"complete.txt");for(int n=1;n<=5;++n)done<<selected.sections[n]->size<<'\n';
}
static void export_arpa(const fs::path&input,const fs::path&output){Model m(input);std::ofstream f(output);require(bool(f),"ARPA output");f<<std::setprecision(9)<<"\\data\\\n";
 for(int n=1;n<=5;++n)f<<"ngram "<<n<<'='<<m.sections[n]->size<<'\n';
 for(int n=1;n<=5;++n){f<<"\n\\"<<n<<"-grams:\n";auto&s=*m.sections[n];for(size_t i=0;i<s.size;++i){auto&r=s.data[i];f<<r.p<<'\t';for(int k=0;k<n;++k){if(k)f<<' ';f<<m.vocab.words[r.w[k]];}if(n<5)f<<'\t'<<r.b;f<<'\n';}std::cout<<"exported order "<<n<<std::endl;}
 f<<"\n\\end\\\n";f.close();require(bool(f),"ARPA write");
}
int main(int argc,char**argv){try{
 require(argc>=2,"mix|prune|export|score");std::string mode=argv[1];
 if(mode=="mix"){require(argc>=7&&argc<=9,"mix A B output alpha threads [resume_order [stop_order]]");materialize(argv[2],argv[3],argv[4],std::stod(argv[5]),std::stoi(argv[6]),argc>=8?std::stoi(argv[7]):2,argc==9?std::stoi(argv[8]):5);}
 else if(mode=="mix3"){require(argc>=7&&argc<=9,"mix3 Corpus4 Articles Third output threads [resume_order [stop_order]]; weights .50/.25/.25");materialize3(argv[2],argv[3],argv[4],argv[5],std::stoi(argv[6]),argc>=8?std::stoi(argv[7]):2,argc==9?std::stoi(argv[8]):5);}
 else if(mode=="prune"){require(argc==8,"prune input output bigrams trigrams fourgrams fivegrams");std::array<uint64_t,6>b{};for(int n=2;n<=5;++n)b[n]=std::stoull(argv[n+2]);prune(argv[2],argv[3],b);}
 else if(mode=="export"){require(argc==4,"export input output");export_arpa(argv[2],argv[3]);}
 else if(mode=="export4"){require(argc==4,"export4 input output");export_four(argv[2],argv[3]);}
 else if(mode=="export5"){require(argc==4,"export5 input output");export_four(argv[2],argv[3],5);}
 else if(mode=="score"){require(argc==3,"score model");Model m(argv[2],false,true);std::string s;std::cout<<std::setprecision(17);while(std::getline(std::cin,s)){std::vector<uint16_t> w;size_t start=0;while(start<s.size()){size_t end=s.find('\t',start);if(end==std::string::npos)end=s.size();w.push_back(m.vocab.id(s.substr(start,end-start)));start=end+1;}require(!w.empty()&&w.size()<=5,"score tokens");std::cout<<m.extended_score(w.data(),w.size())<<'\n';}}
 else throw std::runtime_error("unknown operation");
}catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}}
