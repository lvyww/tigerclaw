"""Windows NLS weekday names compared with explicit .NET globalization backend."""
import argparse
import json
import os
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    parser.add_argument("--globalization", choices=["nls", "icu"], default="nls")
    args = parser.parse_args()
    oracle_env = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_USENLS="true" if args.globalization == "nls" else "false")
    locales = ['', 'en-US', 'en-GB', 'zh-CN', 'zh-TW', 'ja-JP', 'ko-KR', 'de-DE', 'fr-FR', 'es-ES',
               'ru-RU', 'uk-UA', 'pl-PL', 'fi-FI', 'lt-LT', 'lv-LV', 'tr-TR', 'ar-SA', 'fa-IR', 'he-IL',
               'th-TH', 'hi-IN', 'bn-BD', 'vi-VN', 'id-ID', 'el-GR', 'cs-CZ', 'hu-HU', 'is-IS', 'ga-IE']
    cases = [dict(locale=locale, weekday=day) for locale in locales for day in range(7)]
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command, data=payload):
        return subprocess.run(command, input=data, capture_output=True, check=True, env=oracle_env, timeout=120).stdout.splitlines()
    mode = json.loads(run([args.dotnet, args.assembly, '--native-core-text-stdio'], b'{"globalization_probe":true}\n')[-1])
    assert mode['useNls'] == (args.globalization == 'nls'), mode
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    if expected and expected[0] == b'Core startup context tests passed':
        expected = expected[1:]  # Known managed preflight; all remaining lines must be JSON.
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), weekday_culture_parity=True, globalization=args.globalization)))


if __name__ == '__main__':
    main()
