-- Read-only diagnostics over frozen shape decoder cases.
local root,cases,out=arg[1],arg[2],arg[3]
package.path=root..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return root end}
local s=require('tiger_sentence')
assert(s.set_memory_profile('compact'))
s.ensure_lexicon(nil)
assert(s.model_status().loaded)
local f=assert(io.open(out,'w'))
f:write('id\tindex\ttext\tscore\ttarget\tsegments\n')
for line in io.lines(cases) do
 local id,source,raw,target=line:match('([^\t]+)\t([^\t]+)\t([^\t]+)\t(.+)')
 s.reset_decode_cache()
 local results=s.decode(raw,false,'')
 for index,value in ipairs(results) do
  if index<=3 or value.text==target then
   local path=value.path
   local pieces={}
   while path and path.previous do
    local previous=path.previous
    table.insert(pieces,1,raw:sub(previous.raw_length+1,path.raw_length)..'='..
                         path.text:sub(previous.text_length+1))
    path=previous
   end
   f:write(string.format('%s\t%d\t%s\t%.17g\t%s\t%s\n',id,index,value.text,value.score,target,table.concat(pieces,' / ')))
  end
 end
end
f:close()
