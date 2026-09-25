local root,path,words=arg[1],arg[2],arg[3]
package.path=root..'/lua/?.lua;'..package.path
local model=require('tiger_sentence_fivegram').load(path,{page_bytes=64*1024*1024,index_pages=256})
local total,covered=0,0
local missing={}
for line in io.lines(words) do
 local rank,word=line:match('^(%d+)\t([^\t]+)')
 if rank then
  total=total+1;local previous;local ok=true
  for _,point in utf8.codes(word) do
   local char=utf8.char(point)
   if previous and not model.has_observed_bigram(previous,char) then missing[previous..char]=true;ok=false end
   previous=char
  end
  if ok then covered=covered+1 end
 end
end
assert(total==50000)
print('WORDS',total,'COVERED',covered)
local keys={};for k in pairs(missing) do keys[#keys+1]=k end;table.sort(keys)
print('MISSING_UNIQUE_BIGRAMS',#keys)
for _,k in ipairs(keys) do print('MISSING',k) end
for _,word in ipairs({'游戏','安卓','首页','钱包','频道','栏目','时政'}) do
 local chars={};for _,p in utf8.codes(word) do chars[#chars+1]=utf8.char(p) end
 print('GAP_CHECK',word,model.has_observed_bigram(chars[1],chars[2]))
end
model.close()
