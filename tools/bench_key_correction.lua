-- CPU timings; not mobile end-to-end latency. Run each mode in a fresh process.
local pack,data,mode=assert(arg[1]),assert(arg[2]),assert(arg[3])
package.path=pack..'/lua/?.lua;'..package.path
rime_api={get_user_data_dir=function()return data end}
local s=require('tiger_sentence');s.ensure_lexicon();s.set_memory_profile('compact')
assert(s.model_status().loaded)
if arg[6] then s.correction.configure(arg[6]) end
if s.correction then s.correction.set_enabled(mode=='on') end
local repeats=tonumber(arg[4]) or 3
local lengths={}
for value in (arg[5] or '8,16,32,64,128'):gmatch('%d+') do lengths[#lengths+1]=tonumber(value) end
local function percentile(v,p)
    table.sort(v);return v[math.max(1,math.ceil(#v*p))] or 0
end
print('mode\tlength\taction\tcalls\tp50_cpu_ms\tp95_cpu_ms\tmax_lua_kib')
for _,length in ipairs(lengths) do
    local raw=('kispfidyiejryfenahbmsp'):rep(8):sub(1,length)
    local append,backspace,edit={},{},{}
    local peak=0
    local function run(code,times)
        local start=os.clock();s.decode(code,false,'');times[#times+1]=(os.clock()-start)*1000
        peak=math.max(peak,collectgarbage('count'))
    end
    for repeat_index=1,repeats do
        s.reset_decode_cache();collectgarbage('collect')
        for i=1,length do run(raw:sub(1,i),append) end
        for i=length-1,0,-1 do run(raw:sub(1,i),backspace) end
        for _,at in ipairs({1,math.ceil(length/2),length}) do
            local ch=raw:sub(at,at)=='q' and 'w' or 'q'
            run(raw:sub(1,at-1)..ch..raw:sub(at+1),edit)
        end
    end
    for _,entry in ipairs({{'append',append},{'backspace',backspace},{'edit',edit}}) do
        print(string.format('%s\t%d\t%s\t%d\t%.4f\t%.4f\t%.1f',mode,length,entry[1],#entry[2],
            percentile(entry[2],.50),percentile(entry[2],.95),peak))
        io.stdout:flush()
    end
end
