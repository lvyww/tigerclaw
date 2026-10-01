-- Exact scalar/state parity against an independently loaded pre-cache reader.
local pack,baseline,path=assert(arg[1]),assert(arg[2]),assert(arg[3])
local current=dofile(pack..'/lua/tiger_sentence_fivegram.lua').load(path,{page_bytes=2*1024*1024})
local old=dofile(baseline..'/lua/tiger_sentence_fivegram.lua').load(path,{page_bytes=2*1024*1024})
local tokens={'今','天','气','不','错','我','们','测','试','一','下','\2','\3','','not-in-vocab','𠮷'}
local checks=0
current.set_diagnostics(true)
for _,capacity in ipairs({1,8,64,8192})do
    current.configure_cache({context_entries=capacity,logp_entries=capacity})
    for round=1,8 do
        local a={old.bos_id,0,0,0,1};local b={current.bos_id,0,0,0,1}
        for i=1,16 do
            local token=tokens[(i+round-2)%#tokens+1]
            local x={old.step(a[1],a[2],a[3],a[4],a[5],token)}
            local y={current.step(b[1],b[2],b[3],b[4],b[5],token)}
            for k=1,6 do assert(x[k]==y[k],'score/state changed');checks=checks+1 end
            local repeated={current.step(b[1],b[2],b[3],b[4],b[5],token)}
            for k=1,6 do assert(y[k]==repeated[k],'cache hit changed result');checks=checks+1 end
            a={x[2],x[3],x[4],x[5],x[6]};b={y[2],y[3],y[4],y[5],y[6]}
        end
    end
    local status=current.cache_status()
    assert(status.step_entries<=capacity and status.context_entries<=capacity,'unbounded score cache')
    assert(status.page_bytes<=status.page_limit,'metadata pinned model pages')
    current.trim_caches()
    assert(current.cache_status().context_entries==0 and current.cache_status().step_entries==0,'trim failed')
end
assert(current.diagnostics().step_hits>0 and current.diagnostics().context_hits>0,'caches not exercised')
current.close();old.close()
assert(not pcall(current.step,0,0,0,0,1,'天'),'closed model returned a cached score')
print(string.format('{"fivegram_cache_checks":%d,"exact_float_and_state":true}',checks))
