#!/usr/bin/env python3
"""独立的最优单字码与词语编码自动切分整句候选实验程序。

程序仅复用离线实验模型，不连接 TigerClaw.Core，也不会修改输入法配置。
Windows 图形模式会在后台串行解码，输入过程中只展示最新编码的结果。
"""

from __future__ import annotations

import argparse
import os
import queue
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional, Sequence, Tuple

from sentence_neural_reranker import NeuralSentenceScorer
from sentence_variable_decoder import (
    decode_code_lattice,
    encode_shortest_text,
    parse_shortest_code_index,
)
from test_sentence_ngram import (
    CharacterLanguageModel,
    ExperimentError,
    RankedItem,
    WordFrequencyModel,
    rerank_with_words,
)


PROJECT_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SAMPLE_TEXT = "今天早上我吃了两个面包三根油条"


def windows_or_wsl_path(windows: str, wsl: str) -> Path:
    return Path(windows if os.name == "nt" else wsl)


DEFAULT_LEXICON = (
    PROJECT_ROOT / "release_arm64" / "码表" / "虎整句" / "常用字词.txt"
)
DEFAULT_NGRAM_MODEL = windows_or_wsl_path(
    "C:/Archive/tigerclaw_sentence_ml/baseline/ngram-20000.json.gz",
    "/mnt/c/Archive/tigerclaw_sentence_ml/baseline/ngram-20000.json.gz",
)
DEFAULT_WORD_FREQUENCY = windows_or_wsl_path(
    "C:/Users/yc/Nutstore/1/tiger_develop/杂项存档/gen_s5/bin/Debug/net6.0/大词频.txt",
    "/mnt/c/Users/yc/Nutstore/1/tiger_develop/杂项存档/gen_s5/bin/Debug/net6.0/大词频.txt",
)
DEFAULT_NEURAL_MODEL = windows_or_wsl_path(
    "C:/Archive/tigerclaw_sentence_ml/model10m/final-inference.pt",
    "/mnt/c/Archive/tigerclaw_sentence_ml/model10m/final-inference.pt",
)
DEFAULT_VOCABULARY = windows_or_wsl_path(
    "C:/Archive/tigerclaw_sentence_ml/pilot200m/vocabulary.json",
    "/mnt/c/Archive/tigerclaw_sentence_ml/pilot200m/vocabulary.json",
)


@dataclass(frozen=True)
class CandidateView:
    text: str
    segmentation: str
    score: float


@dataclass(frozen=True)
class DecodeResult:
    code: str
    candidates: Tuple[CandidateView, ...]
    character_count: int
    expanded_states: int
    beam_seconds: float
    word_seconds: float
    neural_seconds: float
    total_seconds: float


class SentenceDecoderEngine:
    def __init__(self, args: argparse.Namespace) -> None:
        self._args = args
        self._language_model = CharacterLanguageModel.load(args.ngram_model)
        self._word_model = WordFrequencyModel.load(
            args.word_frequency,
            args.min_word_frequency,
            args.max_word_length,
        )
        self._lexicon = parse_shortest_code_index(args.lexicon)
        self._neural = NeuralSentenceScorer(
            args.neural_model,
            args.vocabulary,
            args.device,
        )

    def encode(self, text: str) -> str:
        return encode_shortest_text(text, self._lexicon)

    def decode(self, code: str) -> DecodeResult:
        started = time.monotonic()
        if not any(character.isalpha() for character in code):
            raise ExperimentError("至少输入一个字母才能产生候选。")
        if len(code) > 128:
            raise ExperimentError("当前神经模型最多处理128码。")

        beam_started = time.monotonic()
        lattice = decode_code_lattice(
            code,
            self._lexicon,
            self._language_model,
            self._args.beam_width,
            self._args.rank_penalty,
        )
        beam_finished = time.monotonic()

        if self._args.word_weight:
            ranked = rerank_with_words(
                lattice.beam, self._word_model, self._args.word_weight
            )
        else:
            ranked = [
                RankedItem(
                    item.score,
                    item,
                    self._word_model.best_segmentation(item.text),
                )
                for item in lattice.beam[: self._args.neural_candidates]
            ]
        word_finished = time.monotonic()

        neural_pool = ranked[: self._args.neural_candidates]
        neural_scores = self._neural.score(
            [item.beam_item.text for item in neural_pool],
            self._args.neural_batch_size,
        )
        def fused_score(value: Tuple[RankedItem, float]) -> float:
            item, neural_score = value
            length_scale = max(len(item.beam_item.text), 1) ** self._args.neural_length_power
            return item.combined_score + self._args.neural_weight * (
                neural_score / length_scale
            )

        fused = sorted(zip(neural_pool, neural_scores), key=fused_score, reverse=True)
        finished = time.monotonic()

        candidates: List[CandidateView] = []
        for item, neural_score in fused[: self._args.candidate_count]:
            segmentation = "/".join(
                token.text if token.frequency else f"[{token.text}]"
                for token in item.word_segmentation.tokens
            )
            candidates.append(
                CandidateView(
                    text=item.beam_item.text,
                    segmentation=segmentation,
                    score=fused_score((item, neural_score)),
                )
            )
        return DecodeResult(
            code=code,
            candidates=tuple(candidates),
            character_count=len(candidates[0].text) if candidates else 0,
            expanded_states=lattice.expanded_states,
            beam_seconds=beam_finished - beam_started,
            word_seconds=word_finished - beam_finished,
            neural_seconds=finished - word_finished,
            total_seconds=finished - started,
        )


def normalized_code(value: str) -> str:
    return "".join(character for character in value.lower() if not character.isspace())


def print_once(args: argparse.Namespace) -> int:
    print("正在加载模型……", flush=True)
    engine = SentenceDecoderEngine(args)
    code = normalized_code(args.decode_once)
    result = engine.decode(code)
    visible_code = result.code.replace(" ", "␠")
    print(
        f"编码: {visible_code}（{len(result.code)}码，首选{result.character_count}字）"
    )
    for rank, candidate in enumerate(result.candidates, 1):
        print(f"{rank:02d}. {candidate.text}\n    {candidate.segmentation}")
    print(
        f"耗时: Beam={result.beam_seconds:.3f}s "
        f"词频={result.word_seconds:.3f}s "
        f"神经={result.neural_seconds:.3f}s "
        f"总计={result.total_seconds:.3f}s"
    )
    return 0


class SentenceInputWindow:
    def __init__(self, args: argparse.Namespace) -> None:
        import tkinter as tk
        from tkinter import ttk

        self._tk = tk
        self._ttk = ttk
        self._args = args
        self._root = tk.Tk()
        self._root.title("TigerClaw 最优码整句候选实验")
        self._root.geometry("900x590")
        self._root.minsize(720, 460)

        self._request_queue: queue.Queue[Optional[Tuple[int, str]]] = queue.Queue()
        self._result_queue: queue.Queue[Tuple[str, object]] = queue.Queue()
        self._generation = 0
        self._debounce_after: Optional[str] = None
        self._ready = False
        self._sample_code = ""
        self._active_code = ""

        self._code = tk.StringVar()
        self._status = tk.StringVar(value="正在加载码表和模型，请稍候……")
        self._detail = tk.StringVar(value="程序不连接输入法运行时，所有计算均在本机完成。")
        self._build_widgets()
        self._code.trace_add("write", self._on_code_changed)
        self._root.protocol("WM_DELETE_WINDOW", self._close)

        self._worker = threading.Thread(target=self._worker_main, daemon=True)
        self._worker.start()
        self._root.after(50, self._poll_results)

    def _build_widgets(self) -> None:
        tk = self._tk
        ttk = self._ttk
        outer = ttk.Frame(self._root, padding=14)
        outer.pack(fill=tk.BOTH, expand=True)

        title = ttk.Label(
            outer,
            text="单字取最优码并允许词语；分号选第二、单引号选第三，数字指定候选位。",
        )
        title.pack(anchor=tk.W, pady=(0, 8))

        input_row = ttk.Frame(outer)
        input_row.pack(fill=tk.X)
        ttk.Label(input_row, text="整句编码：").pack(side=tk.LEFT)
        self._entry = ttk.Entry(input_row, textvariable=self._code, font=("Consolas", 14))
        self._entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(6, 8))
        self._sample_button = ttk.Button(
            input_row, text="载入示例", command=self._load_sample, state=tk.DISABLED
        )
        self._sample_button.pack(side=tk.LEFT, padx=(0, 6))
        ttk.Button(input_row, text="清空", command=lambda: self._code.set("")).pack(
            side=tk.LEFT
        )

        ttk.Label(outer, textvariable=self._status).pack(anchor=tk.W, pady=(10, 2))
        ttk.Label(outer, textvariable=self._detail).pack(anchor=tk.W, pady=(0, 8))

        columns = ("rank", "length", "text", "segmentation", "score")
        self._tree = ttk.Treeview(outer, columns=columns, show="headings")
        self._tree.heading("rank", text="序号")
        self._tree.heading("length", text="字数")
        self._tree.heading("text", text="候选句")
        self._tree.heading("segmentation", text="词频切分")
        self._tree.heading("score", text="融合分")
        self._tree.column("rank", width=55, anchor=tk.CENTER, stretch=False)
        self._tree.column("length", width=55, anchor=tk.CENTER, stretch=False)
        self._tree.column("text", width=280, anchor=tk.W)
        self._tree.column("segmentation", width=350, anchor=tk.W)
        self._tree.column("score", width=100, anchor=tk.E, stretch=False)
        scrollbar = ttk.Scrollbar(outer, orient=tk.VERTICAL, command=self._tree.yview)
        self._tree.configure(yscrollcommand=scrollbar.set)
        self._tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)

        self._entry.focus_set()

    def _load_sample(self) -> None:
        if self._sample_code:
            self._code.set(self._sample_code)
            self._entry.icursor(self._tk.END)
            self._entry.focus_set()

    def _on_code_changed(self, *_: object) -> None:
        raw = normalized_code(self._code.get())
        code_changed = raw != self._active_code
        if code_changed:
            if self._debounce_after is not None:
                self._root.after_cancel(self._debounce_after)
                self._debounce_after = None
            self._active_code = raw
            self._generation += 1
        if not raw:
            self._clear_candidates()
            self._status.set("请输入编码；整串只有一码时才检索一码候选。")
            self._detail.set("可点击“载入示例”查看训练示例。")
            return
        if not self._ready or not code_changed:
            return
        generation = self._generation
        self._debounce_after = self._root.after(
            self._args.debounce_ms,
            lambda: self._submit(generation, raw),
        )

    def _submit(self, generation: int, code: str) -> None:
        self._debounce_after = None
        if generation != self._generation or code != self._active_code:
            return
        self._status.set(f"正在切分并计算 {len(code)} 码候选……")
        self._request_queue.put((generation, code))

    def _worker_main(self) -> None:
        try:
            engine = SentenceDecoderEngine(self._args)
            sample_code = engine.encode(DEFAULT_SAMPLE_TEXT)
        except Exception as ex:  # 后台线程必须把初始化错误送回界面
            self._result_queue.put(("fatal", ex))
            return
        self._result_queue.put(("ready", sample_code))

        while True:
            request = self._request_queue.get()
            if request is None:
                return
            while True:
                try:
                    newer = self._request_queue.get_nowait()
                except queue.Empty:
                    break
                if newer is None:
                    return
                request = newer
            generation, code = request
            try:
                result: object = engine.decode(code)
            except Exception as ex:
                result = ex
            self._result_queue.put(("decoded", (generation, result)))

    def _poll_results(self) -> None:
        try:
            while True:
                kind, payload = self._result_queue.get_nowait()
                if kind == "fatal":
                    self._ready = False
                    self._status.set(f"模型加载失败：{payload}")
                    self._detail.set("请检查启动脚本、模型文件和码表路径。")
                elif kind == "ready":
                    self._ready = True
                    self._sample_code = str(payload)
                    self._sample_button.configure(state=self._tk.NORMAL)
                    self._status.set("模型加载完成，请输入编码。")
                    self._detail.set(
                        f"融合参数：Beam={self._args.beam_width}，"
                        f"词频权重={self._args.word_weight:g}，"
                        f"神经权重={self._args.neural_weight:g}。"
                    )
                    current = normalized_code(self._code.get())
                    if current:
                        self._submit(self._generation, current)
                elif kind == "decoded":
                    generation, result = payload  # type: ignore[misc]
                    if generation != self._generation:
                        continue
                    if isinstance(result, Exception):
                        message = str(result)
                        if "还不能完整切分" in message:
                            self._status.set("末尾编码尚未形成完整单字，等待继续输入。")
                        else:
                            self._clear_candidates()
                            self._status.set(f"无法解码：{message}")
                        continue
                    self._show_result(result)
        except queue.Empty:
            pass
        self._root.after(50, self._poll_results)

    def _show_result(self, result: DecodeResult) -> None:
        self._clear_candidates()
        for rank, candidate in enumerate(result.candidates, 1):
            self._tree.insert(
                "",
                self._tk.END,
                values=(
                    rank,
                    len(candidate.text),
                    candidate.text,
                    candidate.segmentation,
                    f"{candidate.score:.3f}",
                ),
            )
        self._status.set(
            f"{len(result.code)} 码，首选 {result.character_count} 字，"
            f"显示 {len(result.candidates)} 个候选。"
        )
        self._detail.set(
            f"耗时：Beam {result.beam_seconds:.3f}s，词频 {result.word_seconds:.3f}s，"
            f"神经 {result.neural_seconds:.3f}s，总计 {result.total_seconds:.3f}s；"
            f"扩展 {result.expanded_states:,} 个状态。"
        )

    def _clear_candidates(self) -> None:
        for item in self._tree.get_children():
            self._tree.delete(item)

    def _close(self) -> None:
        self._request_queue.put(None)
        self._root.destroy()

    def run(self) -> None:
        self._root.mainloop()


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lexicon", type=Path, default=DEFAULT_LEXICON)
    parser.add_argument("--ngram-model", type=Path, default=DEFAULT_NGRAM_MODEL)
    parser.add_argument("--word-frequency", type=Path, default=DEFAULT_WORD_FREQUENCY)
    parser.add_argument("--neural-model", type=Path, default=DEFAULT_NEURAL_MODEL)
    parser.add_argument("--vocabulary", type=Path, default=DEFAULT_VOCABULARY)
    parser.add_argument("--device", choices=("cpu", "directml"), default="directml")
    parser.add_argument("--beam-width", type=int, default=2000)
    parser.add_argument("--rank-penalty", type=float, default=0.03)
    parser.add_argument("--word-weight", type=float, default=0.0)
    parser.add_argument("--min-word-frequency", type=int, default=300)
    parser.add_argument("--max-word-length", type=int, default=6)
    parser.add_argument("--neural-weight", type=float, default=0.4)
    parser.add_argument("--neural-length-power", type=float, default=0.0)
    parser.add_argument("--neural-candidates", type=int, default=20)
    parser.add_argument("--neural-batch-size", type=int, default=64)
    parser.add_argument("--candidate-count", type=int, default=10)
    parser.add_argument("--debounce-ms", type=int, default=250)
    parser.add_argument(
        "--decode-once",
        help="不打开窗口，按最优单字码和词语编码自动切分并退出。",
    )
    args = parser.parse_args(argv)
    if args.beam_width <= 0 or args.neural_candidates <= 0 or args.candidate_count <= 0:
        parser.error("Beam、神经候选数和显示候选数必须为正数。")
    if args.neural_candidates > args.beam_width:
        parser.error("神经候选数不能大于 Beam 宽度。")
    if args.candidate_count > args.neural_candidates:
        parser.error("显示候选数不能大于神经候选数。")
    return args


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_arguments(argv)
    try:
        if args.decode_once is not None:
            return print_once(args)
        if os.name != "nt":
            raise ExperimentError("图形实时模式请通过 Windows 启动脚本运行。")
        SentenceInputWindow(args).run()
        return 0
    except (ExperimentError, OSError, RuntimeError, ValueError) as ex:
        print(f"错误: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
