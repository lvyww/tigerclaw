"""Dev-only evaluation; no threshold selection from test outcomes."""
import sys
from budget500 import O, decode, rescore, dump

name = sys.argv[1]
model = O / 'models' / (name + '.klm')
size = model.stat().st_size
base = (O / 'models/joint3-q8.klm').stat().st_size
eligibility = {'singleBytes': size, 'combinedBytes': size + base,
               'singleEligible': size <= 500_000_000, 'combinedEligible': size + base <= 500_000_000}
dump(O / (name + '-eligibility.json'), eligibility)
print(name, eligibility, flush=True)
if eligibility['combinedEligible']:
    rescore('dev-' + name + '-rerank', model, O / 'dev3q8.jsonl')
if eligibility['singleEligible']:
    decode('dev-' + name, model, 'dev', 4)
