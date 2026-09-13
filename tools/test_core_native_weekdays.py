"""Windows system-ICU weekday names compared with .NET culture data."""
import argparse
import json
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native', required=True)
    parser.add_argument('--dotnet', required=True)
    parser.add_argument('--assembly', required=True)
    args = parser.parse_args()
    locales = ['', 'en-US', 'en-GB', 'zh-CN', 'zh-TW', 'ja-JP', 'ko-KR', 'de-DE', 'fr-FR', 'es-ES',
               'ru-RU', 'uk-UA', 'pl-PL', 'fi-FI', 'lt-LT', 'lv-LV', 'tr-TR', 'ar-SA', 'fa-IR', 'he-IL',
               'th-TH', 'hi-IN', 'bn-BD', 'vi-VN', 'id-ID', 'el-GR', 'cs-CZ', 'hu-HU', 'is-IS', 'ga-IE']
    cases = [dict(locale=locale, weekday=day) for locale in locales for day in range(7)]
    payload = ''.join(json.dumps(case)+'\n' for case in cases).encode()
    def run(command):
        return subprocess.run(command, input=payload, capture_output=True, check=True, timeout=120).stdout.splitlines()
    expected = run([args.dotnet, args.assembly, '--native-core-text-stdio'])
    actual = run([args.native, '--lexicon-text-stdio'])
    assert len(expected) == len(actual) == len(cases)
    for index, (left, right) in enumerate(zip(expected, actual)):
        assert json.loads(left) == json.loads(right), (cases[index], left, right)
    print(json.dumps(dict(cases=len(cases), weekday_culture_parity=True)))


if __name__ == '__main__':
    main()
