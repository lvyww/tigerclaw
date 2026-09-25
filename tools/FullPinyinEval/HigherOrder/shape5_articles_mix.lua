-- Offline probability mixture: Corpus4 (KenLM or TCS Q8) and Articles TCS Q8.
local alpha=assert(tonumber(os.getenv('ARTICLES_MIX_WEIGHT')))
assert(alpha>=0 and alpha<=1 and alpha==alpha,'invalid Articles weight')
local root=assert(os.getenv('TCS03_READER_ROOT'))
package.path=root..'/lua/?.lua;'..package.path
local reader=require('tiger_sentence_fivegram')
local native
if os.getenv('CORPUS4_MIX_FORMAT')=='tcs03' then
 local corpus
 native={}
 function native.load(path)
  if corpus then corpus.close() end
  corpus=reader.load(path,{page_bytes=64*1024*1024,index_pages=256})
  assert(corpus.format=='TCSKNM03')
  return string.pack('<I2I2I2I2I1',corpus.bos_id,0,0,0,1)
 end
 function native.step(state,target)
  assert(#state==9,'invalid Corpus4 TCS state')
  local a,b,c,d,n=string.unpack('<I2I2I2I2I1',state)
  local score;score,a,b,c,d,n=corpus.step(a,b,c,d,n,target)
  return score,string.pack('<I2I2I2I2I1',a,b,c,d,n)
 end
else
 assert(not os.getenv('CORPUS4_MIX_FORMAT') or os.getenv('CORPUS4_MIX_FORMAT')=='kenlm','invalid Corpus4 format')
 native=assert(package.loadlib(assert(os.getenv('CORPUS4_NATIVE_LIB')),'luaopen_shape5'))()
end
local model, native_bytes
local api={}
local function article_step(state,target)
 local a,b,c,d,n=string.unpack('<I2I2I2I2I1',state)
 local score;score,a,b,c,d,n=model.step(a,b,c,d,n,target)
 return score,string.pack('<I2I2I2I2I1',a,b,c,d,n)
end
function api.load(path)
 if model then model.close();model=nil end
 if alpha==0 then return native.load(path) end
 model=reader.load(assert(os.getenv('ARTICLES_MIX_MODEL')),{page_bytes=64*1024*1024,index_pages=256})
 assert(model.format=='TCSKNM03')
 local b=string.pack('<I2I2I2I2I1',model.bos_id,0,0,0,1)
 if alpha==1 then return b end
 local a=native.load(path);native_bytes=#a
 return a..b
end
function api.step(state,target)
 if alpha==0 then return native.step(state,target) end
 if alpha==1 then return article_step(state,target) end
 assert(#state==native_bytes+9,'invalid mixed state')
 local x,a=native.step(state:sub(1,native_bytes),target)
 local y,b=article_step(state:sub(native_bytes+1),target)
 x=x+math.log(1-alpha);y=y+math.log(alpha)
 local m=math.max(x,y)
 local score=m==-math.huge and m or m+math.log(math.exp(x-m)+math.exp(y-m))
 return score,a..b
end
local loadlib=package.loadlib
package.loadlib=function(path,symbol)
 if path=='articles-mix-offline' and symbol=='luaopen_shape5' then return function()return api end end
 return loadlib(path,symbol)
end
local script=assert(arg[1])
for i=1,#arg do arg[i]=arg[i+1] end
arg[0]=script
dofile(script)
