from pathlib import Path
p=Path('BimeTSF2/SampleIME/BimeTSF2.vcxproj')
b=p.read_bytes()
old=b'      <OptimizeReferences>false</OptimizeReferences>\r\n'
assert b.count(old)==3
assert b'<LinkTimeCodeGeneration>' not in b
p.write_bytes(b.replace(old,old+b'      <LinkTimeCodeGeneration>UseLinkTimeCodeGeneration</LinkTimeCodeGeneration>\r\n'))
Path(__file__).unlink()
print('Set explicit non-incremental LTCG in all three Release link configurations; retained existing NOREF/NOICF.')
