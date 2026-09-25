-- Independent per-token scalar reference, including BOS/EOS and state branching.
local old=assert(package.loadlib(arg[1],'luaopen_shape5'))()
local new=assert(package.loadlib(arg[2],'luaopen_shape5'))()
local mix=assert(package.loadlib(arg[3],'luaopen_shape5'))()
local os0=old.load(os.getenv('SHAPE5_OLD_MODEL'))
local ns0=new.load(os.getenv('SHAPE5_MODEL'))
local ms0=mix.load(os.getenv('SHAPE5_MODEL'))
local w=tonumber(os.getenv('SHAPE5_OLD_WEIGHT'))
local count=0
for text in io.lines(arg[4]) do
 local a,b,c=os0,ns0,ms0
 local tokens={}
 for _,cp in utf8.codes(text) do tokens[#tokens+1]=utf8.char(cp) end
 tokens[#tokens+1]='\3'
 for _,token in ipairs(tokens) do
  local x,y,z;x,a=old.step(a,token);y,b=new.step(b,token);z,c=mix.step(c,token)
  local expected
  if w==1 then expected=x elseif w==0 then expected=y else expected=math.log(w*math.exp(x)+(1-w)*math.exp(y)) end
  assert(math.abs(expected-z)<1e-12, token..' score mismatch')
  count=count+1
 end
end
print('PASS '..count..' independent token scores, old weight '..w)
