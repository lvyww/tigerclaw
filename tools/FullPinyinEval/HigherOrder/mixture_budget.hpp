// Offline, disk-backed probability-model experiments. Never loaded by production.
#pragma once
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <cstdio>
#include <unistd.h>
#ifdef MB_KENLM
#include "lm/model.hh"
#endif
namespace mb {
namespace fs=std::filesystem;
inline void require(bool ok,const std::string& message){if(!ok)throw std::runtime_error(message);}
struct Record {
 uint16_t w[5]{};uint16_t reserved=0;
 float p=0,b=0,loss=-std::numeric_limits<float>::infinity();
};
static_assert(sizeof(Record)==24);
#ifdef MB_KENLM
inline void advise_random_index(const fs::path&path){
 // KenLM requests huge pages even for file mappings. On current Linux kernels
 // they are real file-backed huge pages and amplify sparse trie reads. Adjust
 // only this model's mappings; do not change global VM or upstream libraries.
 struct stat st{};require(stat(path.c_str(),&st)==0,"stat accelerator");
 std::ifstream maps("/proc/self/maps");require(bool(maps),"read index mappings");std::string line;size_t matched=0;
 while(std::getline(maps,line)){
  unsigned long long start,end,offset,inode;unsigned dev_major,dev_minor;char permissions[5]{};
  if(std::sscanf(line.c_str(),"%llx-%llx %4s %llx %x:%x %llu",&start,&end,permissions,&offset,&dev_major,&dev_minor,&inode)!=7)continue;
  if(inode!=st.st_ino||dev_major!=major(st.st_dev)||dev_minor!=minor(st.st_dev))continue;
  require(madvise(reinterpret_cast<void*>(start),end-start,MADV_NOHUGEPAGE)==0,"disable index huge pages");
  require(madvise(reinterpret_cast<void*>(start),end-start,MADV_RANDOM)==0,"random index advice");
  // Drop this read-only mapping's already faulted header PTEs too; the loader
  // may have created a huge mapping before we could override its hint.
  require(madvise(reinterpret_cast<void*>(start),end-start,MADV_DONTNEED)==0,"reset index mapping pages");++matched;
 }
 require(matched>0,"accelerator mapping not found");
}
#endif
inline int compare(const uint16_t* a,const uint16_t* b,int n){for(int i=0;i<n;++i)if(a[i]!=b[i])return a[i]<b[i]?-1:1;return 0;}
inline bool less(const Record&a,const Record&b){return compare(a.w,b.w,5)<0;}
struct Mapping {
 int fd=-1;Record* data=nullptr;size_t size=0;bool writable=false;
 Mapping(const fs::path&p,bool write=false):writable(write){
  fd=open(p.c_str(),write?O_RDWR:O_RDONLY);require(fd>=0,"open "+p.string());
  struct stat st{};require(fstat(fd,&st)==0&&st.st_size%sizeof(Record)==0,"record size "+p.string());size=st.st_size/sizeof(Record);
  if(size){void* v=mmap(nullptr,st.st_size,PROT_READ|(write?PROT_WRITE:0),MAP_SHARED,fd,0);require(v!=MAP_FAILED,"mmap");data=static_cast<Record*>(v);}
 }
 ~Mapping(){if(data)munmap(data,size*sizeof(Record));if(fd>=0)close(fd);}
 Mapping(const Mapping&)=delete;
 void sync(){if(writable&&size)require(msync(data,size*sizeof(Record),MS_SYNC)==0,"msync");}
};
// Direct three-source interpolation of conditional probabilities, before any
// sparse materialization. Fixed experiment weights: Corpus4 .50 / Articles .25 / third .25.
inline double mix_three(double a,double b,double c){
 double m=std::max({a,b,c});
 return m+std::log10(.5*std::pow(10.,a-m)+.25*std::pow(10.,b-m)+.25*std::pow(10.,c-m));
}
struct Vocabulary {
 std::vector<std::string> words;std::unordered_map<std::string,uint16_t> ids;uint16_t unk,bos,eos;
 explicit Vocabulary(const fs::path&p){std::ifstream f(p);require(bool(f),"vocab open");std::string s;
  while(std::getline(f,s)){require(!s.empty()&&words.size()<65535&&!ids.contains(s),"invalid vocabulary");ids[s]=words.size();words.push_back(s);}
  unk=ids.at("<unk>");bos=ids.at("<s>");eos=ids.at("</s>");
 }
 uint16_t id(const std::string&s)const{auto i=ids.find(s);return i==ids.end()?unk:i->second;}
};
struct Model {
 Vocabulary vocab;std::array<std::unique_ptr<Mapping>,6> sections;
 std::array<std::vector<size_t>,6> starts;std::vector<uint8_t> known;size_t unknown_slots=1;
#ifdef MB_KENLM
 std::unique_ptr<lm::ngram::TrieModel> fast4;std::vector<lm::WordIndex> fast_ids;
 std::unique_ptr<lm::ngram::TrieModel> fast5;std::vector<lm::WordIndex> fast5_ids;
#endif
 explicit Model(const fs::path&dir,bool write=false,bool score_only=false):vocab(dir/"vocab.txt"),known(vocab.words.size()){
  for(int n=1;n<=5;++n){
#ifdef MB_KENLM
   // Scoring needs neither the raw highest-order search nor its large startup
   // index when the exact trie is available. Generation/pruning still map all.
   if(score_only&&!write&&fs::exists(dir/"full5.klm")&&(n==5||(n==4&&fs::exists(dir/"lower4.klm"))))continue;
#endif
   auto p=dir/(std::to_string(n)+".bin");if(fs::exists(p))add(n,p,write);
  }
  require(bool(sections[1]),"missing unigrams");for(size_t i=0;i<sections[1]->size;++i)known[sections[1]->data[i].w[0]]=1;
  unknown_slots=1+std::count(known.begin(),known.end(),uint8_t(0));
#ifdef MB_KENLM
  if(fs::exists(dir/"lower4.klm")){
   lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;fast4=std::make_unique<lm::ngram::TrieModel>((dir/"lower4.klm").c_str(),c);require(fast4->Order()==4,"fourgram accelerator order");
   for(auto&s:vocab.words)fast_ids.push_back(fast4->GetVocabulary().Index(s));
   advise_random_index(dir/"lower4.klm");
  }
  if(fs::exists(dir/"full5.klm")){
   lm::ngram::Config c;c.load_method=util::LAZY;c.show_progress=false;fast5=std::make_unique<lm::ngram::TrieModel>((dir/"full5.klm").c_str(),c);require(fast5->Order()==5,"fivegram accelerator order");
   for(auto&s:vocab.words)fast5_ids.push_back(fast5->GetVocabulary().Index(s));
   advise_random_index(dir/"full5.klm");
  }
#endif
 }
 void add(int n,const fs::path&p,bool write=false){
  sections[n]=std::make_unique<Mapping>(p,write);auto& m=*sections[n];auto& ix=starts[n];ix.assign(vocab.words.size()+1,m.size);
  size_t pos=0;for(size_t w=0;w<vocab.words.size();++w){size_t lo=pos,hi=m.size;while(lo<hi){size_t mid=lo+(hi-lo)/2;if(m.data[mid].w[0]<w)lo=mid+1;else hi=mid;}ix[w]=pos=lo;}
 }
 size_t find_index(const uint16_t* w,int n)const{
  auto& m=*sections[n];size_t a=starts[n][w[0]],b=starts[n][w[0]+1];
  while(a<b){size_t mid=a+(b-a)/2;if(compare(m.data[mid].w,w,n)<0)a=mid+1;else b=mid;}
  return a<m.size&&compare(m.data[a].w,w,n)==0?a:m.size;
 }
 Record* find(const uint16_t*w,int n)const{if(!sections[n])return nullptr;size_t i=find_index(w,n);return i==sections[n]->size?nullptr:sections[n]->data+i;}
 double score(const uint16_t*w,int n,bool known_missing=false)const{
  if(n>5){w+=n-5;n=5;}double result=0;
  while(n>1){
#ifdef MB_KENLM
   if(fast5&&n==5&&!known_missing){
    lm::WordIndex reversed[4];for(int i=0;i<4;++i)reversed[i]=fast5_ids[w[3-i]];
    lm::ngram::State next{};auto r=fast5->FullScoreForgotState(reversed,reversed+4,fast5_ids[w[4]],next);
    if(r.ngram_length==5)return result+r.prob;
    known_missing=true;
   }
   if(fast4&&n==4&&!known_missing){
    lm::WordIndex reversed[3];for(int i=0;i<n-1;++i)reversed[i]=fast_ids[w[n-2-i]];
    lm::ngram::State next{};auto target=fast_ids[w[n-1]];
    auto r=fast4->FullScoreForgotState(reversed,reversed+n-1,target,next);
    // Only use an exact highest-order match. Lower trie connector records can
    // be synthetic; keep original records and double accumulation for backoff.
    if(r.ngram_length==4)return result+r.prob;
    known_missing=true;
   }
#endif
   if(!known_missing){if(auto r=find(w,n))return result+r->p;}known_missing=false;
   bool context_possible=true;
#ifdef MB_KENLM
   if(fast4&&n==5){
    // The separate fourgram trie has no synthetic highest-order records.
    // A miss proves this backoff context is absent; avoid the raw-file search.
    lm::WordIndex reversed[3];for(int i=0;i<3;++i)reversed[i]=fast_ids[w[2-i]];
    lm::ngram::State next{};auto r=fast4->FullScoreForgotState(reversed,reversed+3,fast_ids[w[3]],next);
    context_possible=r.ngram_length==4;
    if(context_possible&&fast5){
     lm::WordIndex history[4];for(int i=0;i<4;++i)history[i]=fast5_ids[w[3-i]];
     lm::ngram::State context{};context.backoff[3]=std::numeric_limits<float>::quiet_NaN();
     fast5->GetState(history,history+4,context);
     require(!std::isnan(context.backoff[3]),"exact fourgram context missing in fivegram index");
     // Membership was independently confirmed above. Read the original single
     // float backoff, never a library-accumulated score or a synthetic record.
     result+=context.backoff[3];context_possible=false;
    }
   }
#endif
   if(context_possible){if(auto h=find(w,n-1))result+=h->b;}++w;--n;
  }
  auto r=find(w,1);require(r,"unigram missing");return result+r->p;
 }
 // Extend each source distribution to the union vocabulary by splitting its
 // UNK mass uniformly over UNK and source-unseen types. Histories still map to UNK.
 double extended_score(const uint16_t*w,int n,bool known_missing=false)const{
  uint16_t mapped[5];bool changed=false;for(int i=0;i<n;++i){mapped[i]=known[w[i]]?w[i]:vocab.unk;changed|=mapped[i]!=w[i];}
  double p=score(mapped,n,known_missing&&!changed);if(mapped[n-1]==vocab.unk)p-=std::log10(double(unknown_slots));return p;
 }
 double history_log_mass(const uint16_t*w,int n)const{
  double p=0;for(int k=1;k<=n;++k)if(!(k==1&&w[0]==vocab.bos))p+=score(w,k);return p;
 }
};
inline double mix_log(double x,double y,double a){double m=std::max(x,y);return m+std::log10((1-a)*std::pow(10.,x-m)+a*std::pow(10.,y-m));}
inline double backoff(double mass,double lower){
 require(mass>=-1e-7&&mass<=1+2e-5&&lower>=-1e-7&&lower<=1+2e-5,"invalid conditional mass");
 double r=std::max(0.,1-mass),t=std::max(0.,1-lower);
 if(t<1e-12){require(r<2e-5,"no backoff support");return 0.;}
 return r<=0?-99.:std::log10(r/t);
}
inline void save_records(const fs::path&p,const Record*r,size_t n){std::ofstream f(p,std::ios::binary);require(bool(f),"output open");if(n)f.write(reinterpret_cast<const char*>(r),n*sizeof(Record));f.close();require(bool(f),"output write");}
}
