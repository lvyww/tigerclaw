# Native Overlay renderer reuse experiment

This opt-in Windows benchmark creates hidden, test-owned layered windows. It
does not connect to production IPC, start Core, or deploy to `release_arm64`.
It measures `Renderer::Render` CPU/submission time with QPC, **not** physical
key-to-screen latency or compositor presentation. Formatting occurs outside the
timed interval. Tests use system fonts; missing private fonts exercise fallback,
not an installed private-font collection.

## Reproduction

Before changing the renderer, use `prepare.py` to snapshot its source into an
isolated staging directory. Configure that source with Windows CMake (`-A ARM64`)
and build only `overlay_render_probe`, Release. Build the current probe from
`next/TigerClaw.Overlay.Native` separately. Then, with Windows Python:

```powershell
python tools/OverlayRenderBench/run.py --baseline <baseline-exe> --optimized <new-exe> --output <new-results-directory>
<new-exe> --cache-check
```

The comparison verifies 432 bitmap dimension/BGRA FNV-1a digest pairs across
96/120/144/192 DPI, nine themes, horizontal/vertical candidates, selections,
font/size changes, supplementary Unicode characters and status displays. Every
frame is drawn twice; simulated render-target loss occurs every 13 cases.
`--cache-check` additionally asserts layout/format reuse, invalidation on font,
line-height and status changes, and target recreation with unchanged pixels.

Each timing workload has 50 warmup frames and 1,000 measured frames per run.
Runs use baseline/optimized then optimized/baseline order, totaling 16,000
measured frames. JSON output preserves binary SHA256, raw samples, pixel checks,
resource counts and aggregate quantiles. Avoid concurrent builds during timing.

## 2026-09-08 ARM64 experiment

Windows 11 ARM64, MSVC Release; baseline source snapshot:
`next/_run/OverlayRenderBench/eee47c008f2d/source`. Initial measurements:
`next/_run/OverlayRenderBench/cache-run-1/`. Values below are milliseconds,
aggregated over 2,000 samples per workload and variant.

| Workload | Before p50 / p95 | After p50 / p95 |
| --- | --- | --- |
| Changing window width | 1.1344 / 1.5812 | 0.8709 / 1.2487 |
| Selection only | 0.5759 / 0.7935 | 0.5388 / 0.7288 |
| Theme only | 0.5668 / 0.7592 | 0.5346 / 0.7347 |
| Changing equal-length code | 0.5934 / 0.7992 | 0.5895 / 0.8129 |

Width-changing renders improve about 23% at the median; selection about 6%.
Equal-length typing has no convincing improvement and its tail slightly worsens.
These small absolute renderer savings must not be presented as end-to-end gains.
All 432 size/digest comparisons pass. The final source (including the separate
display-formatting fast path) is rechecked in `cache-final/`: all 432 comparisons
pass again. Both rounds retain about 23% median improvement for resizing
(1.044 -> 0.797 ms and 1.035 -> 0.798 ms). Selection improves about 10–11%;
equal-length typing remains essentially unchanged. `--cache-check` passes.

Per 1,000 warm renders, selection/theme changes now create zero text formats or
layouts (formerly 1,000 each). Width changes still create 1,000 exactly-sized
bitmaps/layouts, but zero formats or render targets (formerly 1,000 each).
This is a single-current-layout cache, not a typed-history cache. DC and target
survive resizing; DIB memory shrinks with the window. Device loss recreates the
target without invalidating device-independent text layout.

Display-only content comparison also avoids rebuilding the formatted candidate
string on position/status/sound-only refreshes after reveal completion. Pending
candidate/annotation deadlines remain active, including overdue timers. This
separate saving is not included in renderer timing above.

Validation also includes ARM64/x64 model and transport CTest suites, ARM64
isolated real-window IPC tests, and 12,000 WPF/native formatter parity cases.
Physical multi-monitor switching and live typing remain user acceptance tests.

The reuse follows Microsoft's [DirectWrite layout guidance](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
and [DC render-target rebinding contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1dcrendertarget-binddc).
