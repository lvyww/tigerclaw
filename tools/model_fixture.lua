-- Synthetic Q8 fivegram layout with an independent table/backoff oracle.
return function(path,extra_tokens)
    local pack=assert(string.pack)
    local cps={0,2,3,65,127,128,2047,2048,0x4e00,0x4e59,0x4eba,0x4f60,0x5929,
        0x597d,0x6211,0x662f,0x7532,0x7684,0x8bdd,0x9fff,0x10000,0x1f600,0x20000,0x10ffff}
    for i=1,(extra_tokens or 0)do cps[#cps+1]=0x30000+i end
    local ids,uni,vocab={},{},{}
    for i,cp in ipairs(cps)do
        local token=i==1 and '<unk>' or i==2 and '<s>' or i==3 and '</s>' or utf8.char(cp)
        ids[token]=i-1;uni[i-1]=(i*7)%256
        vocab[#vocab+1]=pack('<I2',#token)..token..pack('<BB',uni[i-1],0)
    end
    local function id(t)if t=='\2'then return 1 elseif t=='\3'then return 2 end;return ids[t]end
    local tables={[2]={},[3]={},[4]={},[5]={}}
    local function key(h)return table.concat(h,':')end
    for a=0,#cps-1 do
        if a%5~=0 then tables[2][key({a})]={ctx={a},bow=a%15,values={}} end
        for b=0,#cps-1 do
            if (a+b)%3==0 then tables[3][key({a,b})]={ctx={a,b},bow=(a+b)%15,values={}} end
        end
    end
    for order=2,3 do for _,row in pairs(tables[order])do
        local seed=0;for _,v in ipairs(row.ctx)do seed=seed+v end
        for t=0,#cps-1 do if (t+seed)%3==0 then row.values[t]=(t+seed)%7==0 and 0 or (t+seed)*11%256 end end
    end end
    local vocab_raw=table.concat(vocab);local pos=256+4*256*40+#vocab_raw
    local directories,sections,payload={},{},{}
    for order=2,5 do
        local blocks,records=0,0;local directory={}
        for bucket=0,255 do
            local rows={};for _,row in pairs(tables[order])do if row.ctx[1]%256==bucket then rows[#rows+1]=row end end
            table.sort(rows,function(a,b)for i=1,order-1 do if a.ctx[i]~=b.ctx[i]then return a.ctx[i]<b.ctx[i]end end;return false end)
            local start=pos;local chunks,index={},{};local nrecords=0
            for i,row in ipairs(rows)do
                if (i-1)%16==0 then
                    local ix={};for j=1,4 do ix[j]=pack('<I2',row.ctx[j] or 0)end
                    index[#index+1]=table.concat(ix)..pack('<I8',pos)
                end
                local values={};for t,q in pairs(row.values)do values[#values+1]={t,q}end
                table.sort(values,function(a,b)return a[1]<b[1]end)
                local chunk={};for _,v in ipairs(row.ctx)do chunk[#chunk+1]=pack('<I2',v)end
                chunk[#chunk+1]=pack('<BI2',row.bow,#values)
                for _,v in ipairs(values)do chunk[#chunk+1]=pack('<I2B',v[1],v[2])end
                local raw=table.concat(chunk);chunks[#chunks+1]=raw;pos=pos+#raw;nrecords=nrecords+#values
            end
            local offset=pos;local ix=table.concat(index);pos=pos+#ix
            payload[#payload+1]=table.concat(chunks)..ix
            directory[#directory+1]=pack('<I8I8I8I4I4I8',start,offset-start,offset,#index,#rows,nrecords)
            blocks=blocks+#rows;records=records+nrecords
        end
        directories[#directories+1]=table.concat(directory)
        sections[#sections+1]=pack('<I8I8I8',256+(order-2)*256*40,blocks,records)
    end
    local q=pack('<i4I4i4I4',-25500000,10000000,-10000000,5000000)
    local header='TCSKNM03'..pack('<I4I4I8I4I4I4I4I8I8I2I2I2I2',2,256,pos,5,#cps,256,16,256+4*256*40,#vocab_raw,0,1,2,0)..table.concat(sections)..q:rep(5)..string.rep('\0',16)
    assert(#header==256)
    local f=assert(io.open(path,'wb'));f:write(header,table.concat(directories),vocab_raw,table.concat(payload));f:close()
    local function probability(q)return -2.55+q*0.01 end
    local function bow(q)return q==0 and 0 or -1+(q-1)*0.005 end
    local function logp(a,b,c)
        local h={1}
        if a~='\2'then h[#h+1]=id(a)or 0 end
        if b~='\2'or a~='\2'then h[#h+1]=id(b)or 0 end
        local t=id(c)or 0;local total=0
        for n=math.min(4,#h),1,-1 do
            local ctx={};for i=#h-n+1,#h do ctx[#ctx+1]=h[i]end
            local row=tables[n+1][key(ctx)]
            if row then
                if row.values[t]~=nil then return (total+probability(row.values[t]))*math.log(10)end
                total=total+bow(row.bow)
            end
        end
        return (total+probability(uni[t]))*math.log(10)
    end
    return {tokens=cps,bytes=pos,logp=logp,observed=function(a,b)
        local x,y=id(a),id(b);if x==nil or y==nil then return false end
        local row=tables[2][key({x})];return row~=nil and row.values[y]~=nil
    end}
end
