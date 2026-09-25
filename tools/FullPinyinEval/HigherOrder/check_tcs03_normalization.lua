-- Conditional-mass checks over the entire output vocabulary, including UNK/EOS.
local root,path,arpa=arg[1],arg[2],arg[3]
package.path=root..'/lua/?.lua;'..package.path
local model=require('tiger_sentence_fivegram').load(path,{page_bytes=128*1024*1024,index_pages=512})
local f=assert(io.open(path,'rb'));local header=f:read(256);f:close()
local pstep,bstep=0,0
for order=0,4 do
 local pos=161+order*16
 pstep=math.max(pstep,string.unpack('<I4',header,pos+4)/1e12)
 bstep=math.max(bstep,string.unpack('<I4',header,pos+12)/1e12)
end
local bound=math.exp(math.log(10)*(pstep/2+4*bstep/2+5*2e-7))-1+2e-5
local vocab={};local active=false
for line in io.lines(arpa) do
 if line=='\\2-grams:' then break end
 if line=='\\1-grams:' then active=true
 elseif active then
  local _,token=line:match('^(%S+)%s+(%S+)')
  if token and token~='<s>' then vocab[#vocab+1]=token=='</s>' and '\3' or token end
 end
end
assert(#vocab>0)
local texts={'','游戏','愧疚','题主','叶爱雄','科比赛后','生怕','马云雷军','安卓','蓝蕙','钱包','随机上下文','你好','测试模型','𠀀'}
local maximum=0
for _,text in ipairs(texts) do
 local a,b,c,d,n=model.bos_id,0,0,0,1
 for _,cp in utf8.codes(text) do _,a,b,c,d,n=model.step(a,b,c,d,n,utf8.char(cp)) end
 local sum=0
 for _,token in ipairs(vocab) do sum=sum+math.exp(model.step(a,b,c,d,n,token)) end
 maximum=math.max(maximum,math.abs(sum-1))
 print(string.format('context=%s mass=%.12g error=%.8g',text,sum,math.abs(sum-1)))
 assert(math.abs(sum-1)<=bound,'conditional mass outside quantization bound')
end
print(string.format('PASS contexts=%d vocab=%d max_error=%.12g bound=%.12g',#texts,#vocab,maximum,bound))
model.close()
