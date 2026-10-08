#!/usr/bin/env python3
"""Generate C++ defaults from C# authority; fail closed on unsupported expressions."""
import argparse
import json
import pathlib
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
root = pathlib.Path(__file__).resolve().parents[1]
source = (root / 'next/TigerClaw.Core/CoreRuntimeState.cs').read_text(encoding='utf-8-sig')
literal = r'"(?:[^"\\]|\\.)*"'
constants = {'string.Empty': ''}
for name, value in re.findall(r'(?:const|static readonly) string (\w+)\s*=\s*(' + literal + r')\s*;', source):
    constants[name] = json.loads(value)
body = source.split('DefaultConfigPairs = new[]', 1)[1].split('};', 1)[0]
expression = r'(?:' + literal + r'|[\w.]+)'
pairs = re.findall(r'new KeyValuePair<string, string>\(\s*(' + expression + r')\s*,\s*(' + expression + r')\s*\)', body)
assert len(pairs) == body.count('new KeyValuePair<'), 'Unsupported C# default expression'
def resolve(value):
    return json.loads(value) if value.startswith('"') else constants[value]
def cpp(value):
    units = value.encode('utf-16-le')
    return 'u"' + ''.join('\\x%04x' % int.from_bytes(units[i:i+2], 'little') for i in range(0,len(units),2)) + '"'
rows = [(resolve(key), resolve(value)) for key,value in pairs]
assert len({key.casefold() for key,value in rows}) == len(rows)
output = ('// Generated from CoreRuntimeState.DefaultConfigPairs; do not hand-edit.\n'
          '#pragma once\n#include <string_view>\n#include <utility>\nnamespace tiger::core {\n'
          'inline constexpr std::pair<std::u16string_view, std::u16string_view> ConfigDefaults[] = {\n')
output += ''.join('    {' + cpp(key) + ', ' + cpp(value) + '},\n' for key,value in rows)
output += '};\n}\n'
target = root / 'next/TigerClaw.Core.Native/ConfigDefaults.h'
if args.check:
    assert target.read_text() == output, 'Native defaults are stale; run generator'
else:
    target.write_text(output)
print(f'{len(rows)} C# config defaults verified' if args.check else f'Generated {len(rows)} config defaults')
