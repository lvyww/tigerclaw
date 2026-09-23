// Experimental TCSKNM03 version 1 (Q16) -> version 2 (Q8).
// Retains all vocabulary, contexts, successors, and exact-zero backoff codes.
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <vector>
using Bytes=std::vector<uint8_t>;
static void check(bool v,const char* s){if(!v)throw std::runtime_error(s);}
template<class T> T get(const Bytes& b,size_t p){check(p+sizeof(T)<=b.size(),"bounds");T v;std::memcpy(&v,b.data()+p,sizeof v);return v;}
template<class T> void put(Bytes& b,size_t p,T v){check(p+sizeof(T)<=b.size(),"bounds");std::memcpy(b.data()+p,&v,sizeof v);}
template<class T> void add(Bytes& b,T v){size_t p=b.size();b.resize(p+sizeof v);put(b,p,v);}
static Bytes read(std::ifstream& f,uint64_t p,size_t n){Bytes b(n);f.seekg(p);f.read((char*)b.data(),n);check(bool(f),"input read");return b;}
static void write(std::ofstream& f,const Bytes& b){f.write((const char*)b.data(),b.size());check(bool(f),"output write");}
static uint8_t pq(uint16_t q){return uint8_t(std::lround(q/257.0));}
static uint8_t bq(uint16_t q){return q?uint8_t(1+std::lround((q-1)*254.0/65534.0)):0;}
int main(int argc,char**argv)try{
 check(argc==3,"usage: requantize input output");
 check(std::strcmp(argv[1],argv[2])!=0,"input and output must differ");
 std::ifstream in(argv[1],std::ios::binary);check(bool(in),"open input");
 auto h=read(in,0,256);check(std::memcmp(h.data(),"TCSKNM03",8)==0,"magic");
 check(get<uint32_t>(h,8)==1 && get<uint32_t>(h,12)==256,"requires version 1");
 in.seekg(0,std::ios::end);check(uint64_t(in.tellg())==get<uint64_t>(h,16),"input length");
 check(get<uint32_t>(h,24)==5 && get<uint32_t>(h,32)==256,"layout");
 auto original=h;put<uint32_t>(h,8,2);
 // v2 stores step in units of 1e-9; v1's 1e-12 uint32 cannot fit Q8 steps.
 for(int i=0;i<5;i++)for(int k=0;k<2;k++){
  size_t p=160+i*16+4+k*8;
  double scaled=get<uint32_t>(h,p)*(k?65534.0/254.0:257.0)/1000.0;
  check(scaled<=4294967295.0,"step overflow");put<uint32_t>(h,p,uint32_t(std::llround(scaled)));
 }
 auto vocab=read(in,get<uint64_t>(h,40),get<uint64_t>(h,48));Bytes v;
 size_t p=0;for(uint32_t id=0;id<get<uint32_t>(h,28);id++){
  auto len=get<uint16_t>(vocab,p);check(p+len+6<=vocab.size(),"vocab length");
  v.insert(v.end(),vocab.begin()+p,vocab.begin()+p+len+2);p+=len+2;
  add(v,pq(get<uint16_t>(vocab,p)));add(v,bq(get<uint16_t>(vocab,p+2)));p+=4;
 }check(p==vocab.size(),"vocab trailing data");
 std::ofstream out(argv[2],std::ios::binary|std::ios::trunc);check(bool(out),"open output");
 write(out,h);Bytes zeros(4*256*40);write(out,zeros);
 put<uint64_t>(h,40,uint64_t(out.tellp()));put<uint64_t>(h,48,v.size());write(out,v);
 for(int order=2;order<=5;order++){
  auto dir=read(in,get<uint64_t>(original,64+(order-2)*24),256*40);
  uint64_t total_records=0,total_blocks=0;Bytes newdir(256*40);
  for(int bucket=0;bucket<256;bucket++){
   size_t m=bucket*40;auto raw=read(in,get<uint64_t>(dir,m),get<uint64_t>(dir,m+8));
   auto oldidx=read(in,get<uint64_t>(dir,m+16),get<uint32_t>(dir,m+24)*16);
   Bytes blocks,index;size_t pos=0;uint64_t records=0;uint32_t count=get<uint32_t>(dir,m+28);
   uint64_t start=uint64_t(out.tellp());
   for(uint32_t j=0;j<count;j++){
    if(j%get<uint32_t>(h,36)==0){
     size_t oi=index.size();check(oi+16<=oldidx.size(),"index count");
     check(get<uint64_t>(oldidx,oi+8)==get<uint64_t>(dir,m)+pos,"index position");
     index.insert(index.end(),oldidx.begin()+oi,oldidx.begin()+oi+8);add(index,start+blocks.size());
    }
    size_t nctx=(order-1)*2;check(pos+nctx+4<=raw.size(),"block header");
    blocks.insert(blocks.end(),raw.begin()+pos,raw.begin()+pos+nctx);pos+=nctx;
    add(blocks,bq(get<uint16_t>(raw,pos)));auto n=get<uint16_t>(raw,pos+2);add(blocks,n);pos+=4;
    for(unsigned k=0;k<n;k++){add(blocks,get<uint16_t>(raw,pos));add(blocks,pq(get<uint16_t>(raw,pos+2)));pos+=4;}
    records+=n;
   }
   check(pos==raw.size() && index.size()==oldidx.size(),"block/index extent");
   check(records==get<uint64_t>(dir,m+32),"record count");
   put(newdir,m,start);put<uint64_t>(newdir,m+8,blocks.size());
   put<uint64_t>(newdir,m+16,start+blocks.size());put<uint32_t>(newdir,m+24,index.size()/16);
   put(newdir,m+28,count);put(newdir,m+32,records);write(out,blocks);write(out,index);
   total_records+=records;total_blocks+=count;
  }
  check(total_blocks==get<uint64_t>(h,72+(order-2)*24),"section blocks");
  check(total_records==get<uint64_t>(h,80+(order-2)*24),"section records");
  auto end=out.tellp();uint64_t off=256+(order-2)*256*40;
  out.seekp(off);write(out,newdir);out.seekp(end);put(h,64+(order-2)*24,off);
  std::cout<<"order "<<order<<" blocks "<<total_blocks<<" records "<<total_records<<std::endl;
 }
 put<uint64_t>(h,16,uint64_t(out.tellp()));out.seekp(0);write(out,h);out.close();
 std::cout<<"bytes "<<get<uint64_t>(h,16)<<std::endl;
}catch(const std::exception&e){std::cerr<<e.what()<<std::endl;return 1;}
