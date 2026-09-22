"""Strict, complete-run aggregation; dev-only parameter selection."""
import argparse
import json
import math
import hashlib
from pathlib import Path


def read(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        return [json.loads(line) for line in stream]


def decode_rows(path):
    # Segmentation traces stay on disk; aggregate scores do not need millions
    # of segment dictionaries resident at once across six evaluation files.
    rows = []
    with Path(path).open(encoding="utf-8-sig") as stream:
        for line in stream:
            row = json.loads(line)
            row["candidates"] = [{"text": c["text"], "score": c["score"]} for c in row["candidates"]]
            rows.append(row)
    return rows


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_manifests(root, beam):
    """Ensure shards and scorer rows actually belong to the same frozen run."""
    common = None
    for name in [f"dev-{b}" for b in [200, 500, 1000, 2000]] + ["test-words", "test-singles"]:
        output = root / f"{name}.jsonl"
        if not output.exists():
            continue
        manifest = json.loads(Path(str(output) + ".manifest.json").read_text())
        if manifest["mergedSha256"] != digest(output):
            raise ValueError("Merged output hash changed")
        for path, item in manifest.items():
            if path == "mergedSha256":
                continue
            identity = {k: item[k] for k in ["table", "model", "executable", "core"]}
            if common is not None and common != identity:
                raise ValueError("Mixed model/table/program fingerprints")
            common = identity
    scorer = None
    concurrency = {}
    for split, source in [("dev", f"dev-{beam}.jsonl"), ("test", "test-words.jsonl")]:
        source_path = root / source
        if not source_path.exists():
            continue
        source_hash = digest(source_path).upper()
        for path in root.glob(f"qwen-{split}-*.jsonl.manifest.json"):
            item = json.loads(path.read_text())
            identity = {k: item[k] for k in ["host", "gguf", "executable", "top"]}
            if item["input"] != source_hash or item["executable"] != common["executable"]:
                raise ValueError("Qwen output belongs to another input/program")
            if scorer is not None and identity != scorer:
                raise ValueError("Mixed Qwen fingerprints")
            scorer = identity
            concurrency.setdefault(split, set()).add(item["workers"])
    return dict(decoder=common, scorer=scorer,
                qwenWorkers={k: sorted(v) for k, v in concurrency.items()}, qwenThreadsPerWorker=4)


def indexed(rows):
    result = {r["id"]: r for r in rows}
    if len(result) != len(rows):
        raise ValueError("Duplicate decode/case IDs")
    return result


def quantiles(values):
    values = sorted(values)
    if not values:
        return {}
    return {name: values[min(len(values) - 1, math.ceil(p * len(values)) - 1)]
            for name, p in [("p50", .5), ("p95", .95), ("p99", .99), ("max", 1)]}


def accuracy(rows):
    return {f"top{k}": sum(0 < r["rank"] <= k for r in rows) / len(rows) for k in [1, 5, 10, 50]}


def validate(cases, rows, split):
    expected = {i for i, c in cases.items() if c["split"] == split}
    actual = indexed(rows)
    if set(actual) != expected:
        raise ValueError(f"Incomplete/foreign {split} IDs: missing={len(expected-set(actual))}, extra={len(set(actual)-expected)}")
    for i, r in actual.items():
        if r["text"] != cases[i]["text"] or r["code"] != cases[i]["code"]:
            raise ValueError("Case input mismatch")
        if len({c["text"] for c in r["candidates"]}) != len(r["candidates"]):
            raise ValueError("Duplicate candidates")
        expected_rank = next((j + 1 for j, c in enumerate(r["candidates"]) if c["text"] == r["text"]), 0)
        if expected_rank != r["rank"]:
            raise ValueError("Incorrect target rank")
    return actual


def qwen_rows(paths, decode):
    result = {}
    for path in paths:
        for row in read(path):
            if row["error"] is not None:
                continue  # Only a later successful retry may satisfy this ID.
            if row["id"] in result:
                raise ValueError("Duplicate successful Qwen result")
            candidates = decode[row["id"]]["candidates"][:10]
            if row["count"] != len(candidates) or (len(candidates) > 1 and len(row["scores"]) != len(candidates)):
                raise ValueError("Qwen cardinality mismatch")
            if any(not math.isfinite(x) for x in row["scores"]):
                raise ValueError("Non-finite Qwen score")
            result[row["id"]] = row
    if set(result) != set(decode):
        raise ValueError("Incomplete Qwen run")
    return result


def winners(decode, qwen, alpha):
    result = {}
    for i, row in decode.items():
        candidates = row["candidates"][:10]
        if not candidates:
            result[i] = ""
        elif len(candidates) == 1 or alpha == 0:
            result[i] = candidates[0]["text"]
        else:
            best = max(range(len(candidates)), key=lambda j: (1 - alpha) * candidates[j]["score"] + alpha * qwen[i]["scores"][j])
            result[i] = candidates[best]["text"]
    return result


def edit_distance(reference, hypothesis):
    previous = list(range(len(hypothesis) + 1))
    for i, a in enumerate(reference, 1):
        current = [i]
        for j, b in enumerate(hypothesis, 1):
            current.append(min(current[-1] + 1, previous[j] + 1, previous[j-1] + (a != b)))
        previous = current
    return previous[-1]


def evaluate(cases, decode, qwen, alpha):
    chosen = winners(decode, qwen, alpha)
    rescued, regressed, missed, wrong = [], [], [], []
    for i, r in decode.items():
        good = chosen[i] == r["text"]
        detail = dict(id=i, text=r["text"], code=r["code"], rank=r["rank"],
                      ngram=r["candidates"][0]["text"] if r["candidates"] else "", fused=chosen[i])
        if good and r["rank"] != 1:
            rescued.append(detail)
        if not good and r["rank"] == 1:
            regressed.append(detail)
        if not 0 < r["rank"] <= 10:
            missed.append(detail)
        if not good:
            wrong.append(detail)
    covered = [r for i, r in decode.items() if cases[i]["covered"]]
    characters = sum(len(r["text"]) for r in decode.values())
    ngram_errors = sum(edit_distance(r["text"], r["candidates"][0]["text"] if r["candidates"] else "") for r in decode.values())
    fused_errors = sum(edit_distance(r["text"], chosen[i]) for i, r in decode.items())
    return dict(count=len(decode), ngram=accuracy(list(decode.values())), coveredCount=len(covered),
                coveredNgram=accuracy(covered), fusedTop1=1-len(wrong)/len(decode),
                referenceCharacters=characters, ngramCharacterErrorRate=ngram_errors/characters,
                fusedCharacterErrorRate=fused_errors/characters,
                referenceLength=dict(min=min(len(r["text"]) for r in decode.values()),
                    max=max(len(r["text"]) for r in decode.values()), mean=characters/len(decode)),
                rescued=rescued, regressed=regressed, outsideTop10=missed, wrong=wrong,
                decodeMs=quantiles([r["ms"] for r in decode.values()]),
                qwenMs=quantiles([r["ms"] for r in qwen.values()]))


def markdown(report):
    test = report["test"]
    lines = ["# 全拼整句离线实验", "",
             f"开发集 {report['dev']['count']} 句；测试集 {test['count']} 句。",
             f"开发集选定 Beam **{report['beam']}**，Qwen 融合 alpha **{report['alpha']:.2f}**。",
             "参数固定后评测测试集；正确率按整句与唯一参考文本完全一致计算。", "",
             f"测试语料片段长度 {test['referenceLength']['min']}–{test['referenceLength']['max']} 字，平均 {test['referenceLength']['mean']:.2f} 字。", "",
             "## 开发集 Beam 扫描", "", "| Beam | 首选 | Top-5 | Top-10 | Top-50 |",
             "|---:|---:|---:|---:|---:|"]
    for beam, row in report["beamSweep"].items():
        lines.append(f"| {beam} | " + " | ".join(f"{row[f'top{k}']:.3%}" for k in [1,5,10,50]) + " |")
    lines += ["", "## 测试集", "", "| 搜索配置 | 首选 | Top-5 | Top-10 | Top-50 |",
              "|---|---:|---:|---:|---:|"]
    for label, row in [("单字+词组 n-gram", test["ngram"]), ("仅单字 n-gram", report["singlesOnly"]["ngram"])]:
        lines.append(f"| {label} | " + " | ".join(f"{row[f'top{k}']:.3%}" for k in [1,5,10,50]) + " |")
    lines += ["", f"Qwen 融合首选：**{test['fusedTop1']:.3%}**。救回 {len(test['rescued'])} 句，"
              f"退化 {len(test['regressed'])} 句；{len(test['outsideTop10'])} 句的目标不在前十。",
              f"字符编辑错误率（Levenshtein / 参考字数）：n-gram {test['ngramCharacterErrorRate']:.3%}，融合 {test['fusedCharacterErrorRate']:.3%}。",
              f"完整表覆盖的测试样本为 {test['coveredCount']}/{test['count']}；未覆盖项仍计入分母。",
              "", "## 耗时（毫秒）", "", "| 测量 | p50 | p95 | p99 | 最慢 |",
              "|---|---:|---:|---:|---:|"]
    for label, row in [("并行测试集整句 n-gram", test["decodeMs"]), ("并行 Qwen 请求", test["qwenMs"]),
                       ("单进程逐键追加", report["incrementalMs"]["append"]), ("单进程逐键回删", report["incrementalMs"]["backspace"])]:
        lines.append(f"| {label} | " + " | ".join(f"{row[k]:.3f}" for k in ["p50","p95","p99","max"]) + " |")
    lines += ["", f"逐键样本 {report['incrementalSamples']} 句；首次计时按键 {report['firstMeasuredKeyMs']:.3f} ms。",
              f"Qwen 各阶段进程数：{report['fingerprints']['qwenWorkers']}；每进程 4 个计算线程。",
              "逐键测试无其他实验工作进程，但不包含输入法宿主/UI，不能直接视作实际打字延迟。",
              "并行整句和 Qwen 耗时含资源竞争，不与单进程逐键数据混作同一种测量。"]
    for component, values in report.get("memoryBytes", {}).items():
        lines += ["", f"{component}：采集到的单进程最高工作集峰值 {values['maxProcessPeakWorkingSet']/2**20:.1f} MiB；"
                  f"最高采样私有字节 {values['maxSampledProcessPrivateBytes']/2**20:.1f} MiB。"]
    lines += ["", "工作集包含共享模型页，不能直接将各进程工作集相加当作独占内存。", "",
              "## 解释边界", "",
              "- 复用 full-kn-m5-v2，未训练新的拼音模型；码表允许单字与多字词条。",
              "- 语料使用固定版本 pypinyin 自动注音，抽查并修正部分标注，不代表一万句全部审校。",
              "- 测试集与开发集按文本哈希隔离，但未证明与 n-gram/Qwen 训练语料相互独立。",
              "- 同音的其他合理写法也按不匹配计错；这里只衡量唯一参考文本的恢复。",
              "- 这是离线原型，没有部署或修改当前虎爪/Rime/Fcitx5。", "",
              "完整扫描、错误案例、程序/模型哈希见 report.json；逐句候选见 test-words.jsonl，",
              "融合后的候选见 test-fused.jsonl；全部输入、分片和原始日志保存在同目录。", ""]
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("root", type=Path)
    p.add_argument("--select-beam", action="store_true")
    p.add_argument("--select-alpha", action="store_true")
    args = p.parse_args()
    root = args.root
    cases = indexed(read(root / "data/cases.jsonl"))
    dev = {beam: validate(cases, decode_rows(root / f"dev-{beam}.jsonl"), "dev") for beam in [200, 500, 1000, 2000]}
    for width, rows in dev.items():
        if any(r["beam"] != width or not r["words"] for r in rows.values()):
            raise ValueError("Dev parameters disagree with declared sweep")
    sweep = {beam: accuracy(list(rows.values())) for beam, rows in dev.items()}
    best = max(v["top10"] for v in sweep.values())
    beam = min(b for b in sweep if sweep[b]["top10"] >= best - .001 - 1e-12)
    selection = dict(beam=beam, beamSweep=sweep, tolerancePercentagePoints=.1,
                     fingerprints=verify_manifests(root, beam))
    if not args.select_beam:
        qdev = qwen_rows(sorted(root.glob("qwen-dev-*.jsonl")), dev[beam])
        curve = []
        for n in range(21):
            alpha = n / 20
            correct = sum(text == cases[i]["text"] for i, text in winners(dev[beam], qdev, alpha).items())
            curve.append(dict(alpha=alpha, correct=correct, accuracy=correct/len(dev[beam])))
        alpha = max(curve, key=lambda r: (r["correct"], -r["alpha"]))["alpha"]
        selection.update(alpha=alpha, alphaSweep=curve)
        if not args.select_alpha:
            test = validate(cases, decode_rows(root / "test-words.jsonl"), "test")
            singles = validate(cases, decode_rows(root / "test-singles.jsonl"), "test")
            if any(r["beam"] != beam or not r["words"] for r in test.values()):
                raise ValueError("Test parameters do not match dev selection")
            if any(r["beam"] != beam or r["words"] for r in singles.values()):
                raise ValueError("Ablation parameters do not match dev selection")
            qtest = qwen_rows(sorted(root.glob("qwen-test-*.jsonl")), test)
            selection["dev"] = evaluate(cases, dev[beam], qdev, alpha)
            selection["test"] = evaluate(cases, test, qtest, alpha)
            selection["singlesOnly"] = dict(count=len(singles), ngram=accuracy(list(singles.values())),
                coveredCount=sum(cases[i]["singleCovered"] for i in singles),
                coveredNgram=accuracy([r for i, r in singles.items() if cases[i]["singleCovered"]]),
                decodeMs=quantiles([r["ms"] for r in singles.values()]))
            bench = read(root / "latency.jsonl")
            selection["incrementalMs"] = {d: quantiles([r["ms"] for r in bench if r["direction"] == d])
                                           for d in ["append", "backspace"]}
            selection["firstMeasuredKeyMs"] = bench[0]["ms"]
            selection["incrementalSamples"] = len({r["id"] for r in bench})
            if (root / "memory.jsonl").exists():
                memory = read(root / "memory.jsonl")
                selection["memoryBytes"] = {component: dict(maxProcessPeakWorkingSet=max(r["peakWorkingSet"] for r in memory if r["component"] == component),
                    maxSampledProcessPrivateBytes=max(r["privateBytes"] for r in memory if r["component"] == component))
                    for component in sorted({r["component"] for r in memory})}
            selection["coverage"] = json.loads((root / "data/manifest.json").read_text())
    target = "beam-selection.json" if args.select_beam else "alpha-selection.json" if args.select_alpha else "report.json"
    (root / target).write_text(json.dumps(selection, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if "test" in selection:
        (root / "REPORT.md").write_text(markdown(selection), encoding="utf-8")
    print(json.dumps({k: v for k, v in selection.items() if k not in ["dev", "test"]}, ensure_ascii=False, indent=2))
    if "test" in selection:
        print(json.dumps({k: v for k, v in selection["test"].items() if k not in ["rescued", "regressed", "outsideTop10", "wrong"]}, indent=2))


if __name__ == "__main__":
    main()
