#!/usr/bin/env python3
"""用二码候选约束调用网络模型，实验整句候选效果。

该脚本只用于离线实验，不接入 TigerClaw 运行时。模型只会收到整句编码和
每个位置的合法单字集合；使用 --text 时，原始测试句不会发送给模型。
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


DEFAULT_TEXT = "今天早上我吃了两个面包三根油条"
DEFAULT_CODEX_MODEL = "gpt-5.6-sol"
DEFAULT_DEEPSEEK_MODEL = "deepseek-v4-pro"
DEFAULT_DEEPSEEK_BASE_URL = "https://api.deepseek.com"
DEFAULT_DEEPSEEK_KEY_FILE = Path("/mnt/c/Nutstore/GZGS/software/deepseek.txt")
DEFAULT_LEXICON = (
    Path(__file__).resolve().parents[1]
    / "release_arm64"
    / "码表"
    / "B定制-常用"
    / "多多B常用字词.txt"
)


class SentenceExperimentError(Exception):
    """可直接展示给命令行用户的实验错误。"""


@dataclass(frozen=True)
class PrimaryCode:
    code: str
    priority_order: int


@dataclass(frozen=True)
class LexiconIndex:
    primary_code_by_char: Dict[str, PrimaryCode]
    candidates_by_prefix: Dict[str, Tuple[str, ...]]


@dataclass(frozen=True)
class InvalidCandidate:
    text: str
    reason: str


def parse_lexicon(path: Path) -> LexiconIndex:
    if not path.is_file():
        raise SentenceExperimentError(f"码表文件不存在: {path}")

    primary: Dict[str, PrimaryCode] = {}
    try:
        with path.open("r", encoding="utf-8-sig") as stream:
            for source_order, raw_line in enumerate(stream):
                line = raw_line.rstrip("\r\n")
                if not line or line.startswith("---"):
                    continue

                fields = line.split("\t")
                if len(fields) < 2:
                    continue

                text = fields[0]
                code = fields[1].strip().lower()
                if len(text) != 1 or not code:
                    continue

                previous = primary.get(text)
                if previous is None:
                    primary[text] = PrimaryCode(code=code, priority_order=source_order)
                elif len(code) > len(previous.code):
                    primary[text] = PrimaryCode(
                        code=code,
                        priority_order=previous.priority_order,
                    )
    except UnicodeDecodeError as ex:
        raise SentenceExperimentError(f"码表不是有效的 UTF-8 文件: {ex}") from ex
    except OSError as ex:
        raise SentenceExperimentError(f"读取码表失败: {ex}") from ex

    if not primary:
        raise SentenceExperimentError("码表中没有可用的单字编码。")

    grouped: Dict[str, List[Tuple[int, str]]] = defaultdict(list)
    for character, entry in primary.items():
        if len(entry.code) >= 2:
            grouped[entry.code[:2]].append((entry.priority_order, character))

    candidates_by_prefix: Dict[str, Tuple[str, ...]] = {}
    for prefix, values in grouped.items():
        values.sort(key=lambda item: item[0])
        candidates_by_prefix[prefix] = tuple(character for _, character in values)

    return LexiconIndex(
        primary_code_by_char=primary,
        candidates_by_prefix=candidates_by_prefix,
    )


def encode_text(text: str, index: LexiconIndex) -> Tuple[str, ...]:
    if not text:
        raise SentenceExperimentError("测试句不能为空。")

    codes: List[str] = []
    for position, character in enumerate(text, 1):
        entry = index.primary_code_by_char.get(character)
        if entry is None:
            raise SentenceExperimentError(
                f"第 {position} 个字“{character}”在码表中没有单字编码。"
            )
        if len(entry.code) < 2:
            raise SentenceExperimentError(
                f"第 {position} 个字“{character}”的最长编码“{entry.code}”不足两码。"
            )
        codes.append(entry.code[:2])
    return tuple(codes)


def split_code(raw_code: str) -> Tuple[str, ...]:
    code = "".join(raw_code.split()).lower()
    if not code:
        raise SentenceExperimentError("整句编码不能为空。")
    if len(code) % 2 != 0:
        raise SentenceExperimentError(
            f"整句编码长度必须为偶数，当前为 {len(code)} 码。"
        )
    return tuple(code[offset : offset + 2] for offset in range(0, len(code), 2))


def resolve_candidate_sets(
    codes: Sequence[str], index: LexiconIndex
) -> Tuple[Tuple[str, ...], ...]:
    resolved: List[Tuple[str, ...]] = []
    for position, code in enumerate(codes, 1):
        candidates = index.candidates_by_prefix.get(code)
        if not candidates:
            raise SentenceExperimentError(
                f"第 {position} 组二码“{code}”没有可用单字。"
            )
        resolved.append(candidates)
    return tuple(resolved)


def build_prompt(
    codes: Sequence[str],
    candidate_sets: Sequence[Sequence[str]],
    candidate_count: int,
) -> str:
    payload = {
        "code": "".join(codes),
        "candidate_count": candidate_count,
        "positions": [
            {
                "position": index + 1,
                "code": code,
                "allowed_characters": "".join(candidates),
            }
            for index, (code, candidates) in enumerate(zip(codes, candidate_sets))
        ],
    }
    return (
        "你是中文输入法的整句解码器。这是一个闭集候选排序任务，"
        "不要调用工具，不要读取文件，只根据下面的 JSON 数据作答。\n"
        f"请返回最多 {candidate_count} 个不重复的现代中文整句，按自然度从高到低排列。\n"
        f"每个句子必须恰好包含 {len(codes)} 个 Unicode 字符，不得添加空格、标点或解释。\n"
        "第 i 个字符必须从 positions 中第 i 项的 allowed_characters 选取，"
        "不得使用集合外的字。\n"
        "优先选择符合现代汉语语法、语义连贯且日常常用的句子。\n"
        "请严格返回 JSON 对象，格式示例："
        '{"candidates":["候选句一","候选句二"]}。\n\n'
        + json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
    )


def build_output_schema(candidate_count: int) -> dict:
    return {
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "properties": {
            "candidates": {
                "type": "array",
                "minItems": 1,
                "maxItems": candidate_count,
                "items": {"type": "string"},
            }
        },
        "required": ["candidates"],
        "additionalProperties": False,
    }


def run_codex(
    codex_binary: str,
    model: str,
    reasoning_effort: str,
    prompt: str,
    candidate_count: int,
    timeout_seconds: int,
    verbose: bool,
) -> Tuple[List[str], float]:
    resolved_binary = shutil.which(codex_binary)
    if resolved_binary is None:
        raise SentenceExperimentError(
            f"找不到 Codex CLI“{codex_binary}”，请先安装并登录 Codex。"
        )

    with tempfile.TemporaryDirectory(prefix="tigerclaw-sentence-codex-") as temp_dir:
        temp_path = Path(temp_dir)
        schema_path = temp_path / "output-schema.json"
        schema_path.write_text(
            json.dumps(build_output_schema(candidate_count), ensure_ascii=False),
            encoding="utf-8",
        )

        command = [
            resolved_binary,
            "exec",
            "--ephemeral",
            "--sandbox",
            "read-only",
            "--ignore-user-config",
            "--ignore-rules",
            "--skip-git-repo-check",
            "--model",
            model,
            "--config",
            f'model_reasoning_effort="{reasoning_effort}"',
            "--output-schema",
            str(schema_path),
            "--cd",
            str(temp_path),
            "-",
        ]

        started = time.monotonic()
        try:
            completed = subprocess.run(
                command,
                input=prompt,
                text=True,
                encoding="utf-8",
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=timeout_seconds,
                check=False,
            )
        except subprocess.TimeoutExpired as ex:
            raise SentenceExperimentError(
                f"Codex 调用超过 {timeout_seconds} 秒未完成。"
            ) from ex
        except OSError as ex:
            raise SentenceExperimentError(f"启动 Codex CLI 失败: {ex}") from ex

        elapsed = time.monotonic() - started
        if verbose and completed.stderr:
            print("\n--- Codex stderr ---", file=sys.stderr)
            print(completed.stderr.rstrip(), file=sys.stderr)

        if completed.returncode != 0:
            detail = completed.stderr.strip() or completed.stdout.strip() or "未返回错误详情"
            raise SentenceExperimentError(
                f"Codex CLI 调用失败（退出码 {completed.returncode}）: {detail}"
            )

        raw_output = completed.stdout.strip()
        try:
            parsed = json.loads(raw_output)
        except json.JSONDecodeError as ex:
            raise SentenceExperimentError(
                f"Codex 返回的不是有效 JSON: {raw_output[:500]}"
            ) from ex

        candidates = parsed.get("candidates") if isinstance(parsed, dict) else None
        if not isinstance(candidates, list) or not all(isinstance(item, str) for item in candidates):
            raise SentenceExperimentError("Codex JSON 中缺少有效的 candidates 字符串数组。")
        return candidates, elapsed


def load_api_key(path: Path) -> str:
    if not path.is_file():
        raise SentenceExperimentError(f"DeepSeek API Key 文件不存在: {path}")
    try:
        key = path.read_text(encoding="utf-8-sig").strip()
    except (OSError, UnicodeDecodeError) as ex:
        raise SentenceExperimentError(f"读取 DeepSeek API Key 文件失败: {ex}") from ex
    if not key:
        raise SentenceExperimentError("DeepSeek API Key 文件为空。")
    if any(character.isspace() for character in key):
        raise SentenceExperimentError("DeepSeek API Key 必须是不含空白的单行文本。")
    return key


def run_deepseek(
    api_key_file: Path,
    base_url: str,
    model: str,
    reasoning_effort: str,
    prompt: str,
    timeout_seconds: int,
    max_tokens: int,
    empty_content_retries: int,
) -> Tuple[List[str], float]:
    normalized_base_url = base_url.rstrip("/")
    if not normalized_base_url.startswith("https://"):
        raise SentenceExperimentError("DeepSeek Base URL 必须使用 HTTPS，避免泄露 API Key。")

    api_key = load_api_key(api_key_file)
    effective_effort = "high" if reasoning_effort == "medium" else reasoning_effort
    body = {
        "model": model,
        "messages": [
            {
                "role": "system",
                "content": (
                    "你是只输出 JSON 的中文输入法整句解码器。"
                    "输出必须是包含 candidates 字符串数组的 JSON 对象。"
                ),
            },
            {"role": "user", "content": prompt},
        ],
        "response_format": {"type": "json_object"},
        "max_tokens": max_tokens,
    }
    if effective_effort == "none":
        body["thinking"] = {"type": "disabled"}
    else:
        if effective_effort not in ("low", "high", "xhigh", "max"):
            effective_effort = "high"
        body["thinking"] = {"type": "enabled"}
        body["reasoning_effort"] = effective_effort

    started = time.monotonic()
    content = ""
    finish_reason = "unknown"
    for attempt in range(empty_content_retries + 1):
        attempt_body = dict(body)
        attempt_body["messages"] = list(body["messages"])
        if attempt > 0:
            attempt_body["messages"].append(
                {
                    "role": "user",
                    "content": (
                        "上一次返回了空 content。请立即返回非空 JSON，"
                        '格式必须为 {"candidates":["候选句"]}。'
                    ),
                }
            )
        request = urllib.request.Request(
            normalized_base_url + "/chat/completions",
            data=json.dumps(attempt_body, ensure_ascii=False).encode("utf-8"),
            headers={
                "Authorization": "Bearer " + api_key,
                "Content-Type": "application/json",
                "Accept": "application/json",
            },
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
                response_body = response.read().decode("utf-8")
        except urllib.error.HTTPError as ex:
            try:
                detail = ex.read().decode("utf-8", errors="replace")
            except OSError:
                detail = ""
            message = detail[:1000] if detail else str(ex.reason)
            raise SentenceExperimentError(f"DeepSeek API 返回 HTTP {ex.code}: {message}") from ex
        except urllib.error.URLError as ex:
            raise SentenceExperimentError(f"连接 DeepSeek API 失败: {ex.reason}") from ex
        except TimeoutError as ex:
            raise SentenceExperimentError(
                f"DeepSeek API 调用超过 {timeout_seconds} 秒未完成。"
            ) from ex
        except OSError as ex:
            raise SentenceExperimentError(f"读取 DeepSeek API 响应失败: {ex}") from ex

        try:
            envelope = json.loads(response_body)
            choice = envelope["choices"][0]
            content = choice["message"]["content"]
            finish_reason = str(choice.get("finish_reason") or "unknown")
        except (json.JSONDecodeError, KeyError, IndexError, TypeError) as ex:
            raise SentenceExperimentError(
                f"DeepSeek API 返回结构无法解析: {response_body[:500]}"
            ) from ex
        if isinstance(content, str) and content.strip():
            break
    else:
        raise SentenceExperimentError(
            "DeepSeek API 连续返回空 content"
            f"（finish_reason={finish_reason}）。"
        )
    elapsed = time.monotonic() - started

    try:
        parsed = json.loads(content)
    except json.JSONDecodeError as ex:
        raise SentenceExperimentError(
            f"DeepSeek content 不是有效 JSON: {content[:500]}"
        ) from ex
    candidates = parsed.get("candidates") if isinstance(parsed, dict) else None
    if not isinstance(candidates, list) or not all(isinstance(item, str) for item in candidates):
        raise SentenceExperimentError("DeepSeek JSON 中缺少有效的 candidates 字符串数组。")
    return candidates, elapsed


def validate_candidates(
    candidates: Iterable[str], candidate_sets: Sequence[Sequence[str]]
) -> Tuple[List[str], List[InvalidCandidate]]:
    valid: List[str] = []
    invalid: List[InvalidCandidate] = []
    seen = set()
    allowed_sets = [set(values) for values in candidate_sets]

    for text in candidates:
        if text in seen:
            invalid.append(InvalidCandidate(text=text, reason="候选重复"))
            continue
        seen.add(text)

        if len(text) != len(allowed_sets):
            invalid.append(
                InvalidCandidate(
                    text=text,
                    reason=f"字符数为 {len(text)}，期望 {len(allowed_sets)}",
                )
            )
            continue

        violation: Optional[str] = None
        for position, (character, allowed) in enumerate(zip(text, allowed_sets), 1):
            if character not in allowed:
                violation = f"第 {position} 个字“{character}”不在对应二码候选中"
                break
        if violation is not None:
            invalid.append(InvalidCandidate(text=text, reason=violation))
            continue

        valid.append(text)

    return valid, invalid


def print_input_summary(
    lexicon_path: Path,
    codes: Sequence[str],
    candidate_sets: Sequence[Sequence[str]],
    expected_text: Optional[str],
) -> None:
    print(f"码表: {lexicon_path}")
    if expected_text is not None:
        print(f"本地校验句（不发送给模型）: {expected_text}")
    print(f"分组编码: {' '.join(codes)}")
    print(f"整句编码: {''.join(codes)}")
    print(f"字数: {len(codes)}")
    print("\n各位置合法字集:")
    for position, (code, candidates) in enumerate(zip(codes, candidate_sets), 1):
        target_note = ""
        if expected_text is not None:
            target = expected_text[position - 1]
            rank = candidates.index(target) + 1 if target in candidates else 0
            target_note = f"  校验字={target} 码表位次={rank or '-'}"
        print(
            f"{position:02d}. {code}  候选数={len(candidates):2d}  "
            f"{''.join(candidates)}{target_note}"
        )


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="使用二码单字集合约束网络大模型，测试中文整句候选效果。"
    )
    parser.add_argument("--lexicon", type=Path, default=DEFAULT_LEXICON, help="原始码表路径")
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--text", help="本地测试句；脚本自动反查二码")
    source.add_argument("--code", help="直接提供偶数长度的整句二码串")
    parser.add_argument("--count", type=int, default=10, help="要求返回的候选数（默认 10）")
    parser.add_argument(
        "--provider",
        choices=("codex", "deepseek"),
        default="codex",
        help="网络模型提供方（默认 codex）",
    )
    parser.add_argument("--model", help="模型名；留空时按 provider 选择默认值")
    parser.add_argument(
        "--reasoning-effort",
        choices=("none", "low", "medium", "high", "xhigh", "max"),
        default="high",
        help="模型推理强度（默认 high）",
    )
    parser.add_argument("--timeout", type=int, default=180, help="网络模型超时秒数（默认 180）")
    parser.add_argument("--codex-bin", default="codex", help="Codex CLI 命令名或路径")
    parser.add_argument(
        "--api-key-file",
        type=Path,
        default=DEFAULT_DEEPSEEK_KEY_FILE,
        help="DeepSeek API Key 文件",
    )
    parser.add_argument(
        "--deepseek-base-url",
        default=DEFAULT_DEEPSEEK_BASE_URL,
        help="DeepSeek OpenAI 兼容 API 地址",
    )
    parser.add_argument(
        "--deepseek-max-tokens",
        type=int,
        default=8192,
        help="DeepSeek 思考与输出 token 上限（默认 8192）",
    )
    parser.add_argument(
        "--deepseek-empty-retries",
        type=int,
        default=1,
        help="DeepSeek 返回空 content 时的重试次数（默认 1）",
    )
    parser.add_argument("--dry-run", action="store_true", help="只显示输入和提示词，不调用网络模型")
    parser.add_argument("--verbose", action="store_true", help="显示 Codex CLI 进度与诊断输出")
    args = parser.parse_args(argv)

    if not 1 <= args.count <= 20:
        parser.error("--count 必须在 1 到 20 之间")
    if args.timeout <= 0:
        parser.error("--timeout 必须大于 0")
    if args.deepseek_max_tokens <= 0:
        parser.error("--deepseek-max-tokens 必须大于 0")
    if not 0 <= args.deepseek_empty_retries <= 3:
        parser.error("--deepseek-empty-retries 必须在 0 到 3 之间")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_arguments(argv)
    expected_text: Optional[str]
    try:
        index = parse_lexicon(args.lexicon)
        if args.code is not None:
            codes = split_code(args.code)
            expected_text = None
        else:
            expected_text = args.text if args.text is not None else DEFAULT_TEXT
            codes = encode_text(expected_text, index)

        candidate_sets = resolve_candidate_sets(codes, index)
        prompt = build_prompt(codes, candidate_sets, args.count)
        print_input_summary(args.lexicon, codes, candidate_sets, expected_text)

        if args.dry_run:
            print("\n--- 发送给网络模型的提示词 ---")
            print(prompt)
            return 0

        model = args.model or (
            DEFAULT_DEEPSEEK_MODEL if args.provider == "deepseek" else DEFAULT_CODEX_MODEL
        )
        print(
            f"\n正在调用 {args.provider}/{model}"
            f"（reasoning={args.reasoning_effort}）……",
            flush=True,
        )
        if args.provider == "deepseek":
            raw_candidates, elapsed = run_deepseek(
                api_key_file=args.api_key_file,
                base_url=args.deepseek_base_url,
                model=model,
                reasoning_effort=args.reasoning_effort,
                prompt=prompt,
                timeout_seconds=args.timeout,
                max_tokens=args.deepseek_max_tokens,
                empty_content_retries=args.deepseek_empty_retries,
            )
        else:
            raw_candidates, elapsed = run_codex(
                codex_binary=args.codex_bin,
                model=model,
                reasoning_effort=args.reasoning_effort,
                prompt=prompt,
                candidate_count=args.count,
                timeout_seconds=args.timeout,
                verbose=args.verbose,
            )
        valid, invalid = validate_candidates(raw_candidates, candidate_sets)

        print(f"\n合法整句候选（{len(valid)} 条）:")
        for rank, candidate in enumerate(valid, 1):
            target_note = "  <-- 命中本地校验句" if candidate == expected_text else ""
            print(f"{rank:02d}. {candidate}{target_note}")

        if not valid:
            print("（无）")

        if invalid:
            print(f"\n被本地校验拒绝的输出（{len(invalid)} 条）:")
            for item in invalid:
                print(f"- {item.text!r}: {item.reason}")

        if expected_text is not None:
            expected_rank = valid.index(expected_text) + 1 if expected_text in valid else 0
            print(f"\n本地校验句排名: {expected_rank if expected_rank else '未进入候选'}")
        print(f"模型调用耗时: {elapsed:.2f} 秒")
        return 0 if valid else 2
    except SentenceExperimentError as ex:
        print(f"错误: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
