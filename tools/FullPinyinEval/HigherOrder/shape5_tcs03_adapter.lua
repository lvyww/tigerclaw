-- Offline adapter for the unchanged frozen decoder's opaque-state fivegram API.
-- Usage: lua this.lua target-script.lua [target arguments ...]
local root=assert(os.getenv('TCS03_READER_ROOT'))
package.path=root..'/lua/?.lua;'..package.path
local reader=require('tiger_sentence_fivegram')
local model
local api={}
function api.load(path)
 if model then model.close() end
 model=reader.load(path,{page_bytes=64*1024*1024,index_pages=256})
 assert(model.format=='TCSKNM03')
 return string.pack('<I2I2I2I2I1',model.bos_id,0,0,0,1)
end
function api.step(state,target)
 assert(#state==9,'bad TCS03 adapter state')
 local a,b,c,d,n=string.unpack('<I2I2I2I2I1',state)
 local score;score,a,b,c,d,n=model.step(a,b,c,d,n,target)
 return score,string.pack('<I2I2I2I2I1',a,b,c,d,n)
end
local native_load=package.loadlib
package.loadlib=function(path,symbol)
 if path=='tcs03-offline' and symbol=='luaopen_shape5' then return function()return api end end
 return native_load(path,symbol)
end
local script=assert(arg[1])
for i=1,#arg do arg[i]=arg[i+1] end
arg[0]=script
dofile(script)
