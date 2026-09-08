#!/usr/bin/env python3
"""Compare the actual WPF and native formatters; no production IPC or UI."""
import argparse
import json
import random
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--assembly", required=True, help="TigerClaw.Core.Tests.dll")
    parser.add_argument("--native", required=True, help="overlay_format_probe executable")
    parser.add_argument("--count", type=int, default=12000)
    args = parser.parse_args()
    rng = random.Random(20260908)
    texts = ["", "a", "AbCdEfGh", "可", "可以", "𠀀", "a\r\nb\rc\nd\te", "e\u0301", "👨‍👩‍👦", "\"\\", " "]
    cases = []
    for i in range(args.count):
        state = {key: bool(rng.getrandbits(1)) for key in (
            "CandidateVisible", "VerticalCandidates", "ShowCandidateIndex", "HideCandidateItems",
            "ShowInputCodeInCandidateWindow", "IsNativeHook")}
        state.update(
            InputCode=rng.choice(texts), Candidates=[rng.choice(texts) for _ in range(rng.randrange(7))],
            CandidateAnnotations=[rng.choice(texts) for _ in range(rng.randrange(7))],
            CompositionState=rng.randrange(7), SelectedCandidateIndex=rng.randrange(-1, 8),
            CandidateExpandDelayMs=rng.choice([-1, 0, 1, 100]), CodeMasking=rng.choice(["", "*"]))
        if i % 11 == 0:
            state["Candidates"] = None
        if i % 13 == 0:
            state["CandidateAnnotations"] = None
        cases.append(dict(state=state, expanded=bool(rng.getrandbits(1)), annotations=bool(rng.getrandbits(1))))
    payload = "".join(json.dumps(case, ensure_ascii=True) + "\n" for case in cases)
    def run(command):
        process = subprocess.run(command, input=payload, text=True, encoding="utf-8", capture_output=True, timeout=120, check=True)
        results = [json.loads(line) for line in process.stdout.splitlines()]
        if len(results) != len(cases):
            raise AssertionError(f"Expected {len(cases)} outputs, got {len(results)}")
        return results
    managed = run([args.dotnet, args.assembly, "--overlay-format-stdio"])
    native = run([args.native])
    for index, (left, right) in enumerate(zip(managed, native)):
        if left != right:
            raise AssertionError(json.dumps(dict(index=index, case=cases[index], wpf=left, native=right), ensure_ascii=False))
    print(f"WPF/native formatter parity passed: {len(cases)} cases")


if __name__ == "__main__":
    main()
