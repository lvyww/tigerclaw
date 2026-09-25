-- Independent scalar arithmetic oracle. Run through shape5_articles_mix.lua.
local native
if os.getenv('CORPUS4_MIX_FORMAT')=='tcs03' then
 local corpus=require('tiger_sentence_fivegram').load(os.getenv('SHAPE5_MODEL'),{page_bytes=64*1024*1024,index_pages=256})
 native={load=function()return {corpus.bos_id,0,0,0,1} end}
 function native.step(s,t)
  local v,a,b,c,d,n=corpus.step(s[1],s[2],s[3],s[4],s[5],t)
  return v,{a,b,c,d,n}
 end
else
 native=assert(package.loadlib(os.getenv('CORPUS4_NATIVE_LIB'),'luaopen_shape5'))()
end
local tcs=require('tiger_sentence_fivegram').load(os.getenv('ARTICLES_MIX_MODEL'),{page_bytes=64*1024*1024,index_pages=256})
local mix=assert(package.loadlib('articles-mix-offline','luaopen_shape5'))()
local ns=native.load(os.getenv('SHAPE5_MODEL'))
local ms=mix.load(os.getenv('SHAPE5_MODEL'))
local alpha=tonumber(os.getenv('ARTICLES_MIX_WEIGHT'))
local tested=0
for line in io.lines(arg[1]) do
 local n,m=ns,ms
 local a,b,c,d,k=tcs.bos_id,0,0,0,1
 local tokens={}
 for _,cp in utf8.codes(line) do tokens[#tokens+1]=utf8.char(cp) end
 tokens[#tokens+1]='\3'
 for _,token in ipairs(tokens) do
  local x,y,z
  x,n=native.step(n,token)
  y,a,b,c,d,k=tcs.step(a,b,c,d,k,token)
  z,m=mix.step(m,token)
  local expected=alpha==0 and x or alpha==1 and y or math.log((1-alpha)*math.exp(x)+alpha*math.exp(y))
  assert(math.abs(z-expected)<1e-12,'scalar mixture mismatch')
  tested=tested+1
 end
end
tcs.close()
print('PASS scalar tokens '..tested..' Articles alpha '..alpha)
