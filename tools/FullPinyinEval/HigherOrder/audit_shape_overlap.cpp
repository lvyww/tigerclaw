// Exact tokenized-line overlap only; not a substring or source-record audit.
#include <fstream>
#include <iostream>
#include <string>
#include <unordered_map>
#include <vector>
struct Target {std::string id; unsigned long long count=0;};
int main(int argc,char**argv){
 if(argc!=4)return 2;
 std::vector<char> buffer(4*1024*1024);
 std::ifstream patterns(argv[1]),train;train.rdbuf()->pubsetbuf(buffer.data(),buffer.size());train.open(argv[2]);std::ofstream output(argv[3]);
 if(!patterns||!train||!output)return 3;
 std::unordered_map<std::string,std::vector<Target>> index;std::string line;
 while(std::getline(patterns,line)){
  auto tab=line.find('\t');if(tab==std::string::npos)return 4;
  index[line.substr(tab+1)].push_back({line.substr(0,tab),0});
 }
 unsigned long long rows=0;
 while(std::getline(train,line)){
  auto it=index.find(line);if(it!=index.end())for(auto &x:it->second)x.count++;
  rows++;
 }
 if(!train.eof())return 5;
 output<<"id\texact_training_occurrences\n";
 for(auto &p:index)for(auto &x:p.second)output<<x.id<<'\t'<<x.count<<'\n';
 std::cout<<"training_rows "<<rows<<'\n';
}
