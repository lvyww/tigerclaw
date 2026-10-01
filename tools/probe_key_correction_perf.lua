-- Small development-only comparison. Not the full heldout accuracy sweep.
local pack,data,cases,profile,output=arg[1],arg[2],arg[3],arg[4],arg[5]
package.path=pack..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return data end}
local s=require('tiger_sentence');s.ensure_lexicon();s.set_memory_profile('compact')
assert(s.model_status().loaded)
local c=s.correction
if c.configure then c.configure(profile) else assert(profile=='reference') end
c.set_enabled(true)
local f=assert(io.open(output,'w'))
f:write('id\tdataset\tvariant\tprofile\tcorrect\ttop5\tcorrections\tincomplete\tcpu_ms\tfirst\n')
for line in io.lines(cases) do
    local v={};for value in line:gmatch('[^\t]+')do v[#v+1]=value end
    for variant,index in ipairs({3,5,6})do
        local raw=v[index]
        if raw~='-' then
            s.reset_decode_cache()
            local start=os.clock();local result=s.decode(raw,false,'');local elapsed=(os.clock()-start)*1000
            local top5,count=false,0
            for i,item in ipairs(result)do
                if i<=5 and item.text==v[4] then top5=true end
                if item.correction_count then count=count+1 end
            end
            local first=result[1] and result[1].text or ''
            f:write(string.format('%s\t%s\t%d\t%s\t%d\t%d\t%d\t%d\t%.4f\t%s\n',
                v[1],v[2],variant-1,profile,first==v[4] and 1 or 0,top5 and 1 or 0,count,
                result.correction_incomplete and 1 or 0,elapsed,first))
        end
    end
    f:flush()
end
f:close()
