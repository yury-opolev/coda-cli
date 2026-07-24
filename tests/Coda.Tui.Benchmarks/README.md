# Coda TUI performance harness (Workstream 0)

A BenchmarkDotNet project that turns the TUI/serve responsiveness targets into repeatable,
gated measurements. It exists so architectural decisions (Workstreams 1–5) are driven by
evidence, not assumptions, and so regressions are caught automatically.

## Run it

Full statistical run (authoritative — use for decisions):

```
dotnet run -c Release --project tests/Coda.Tui.Benchmarks
```

One area only (BenchmarkDotNet `--filter` glob):

```
dotnet run -c Release --project tests/Coda.Tui.Benchmarks -- --filter *Composer*
dotnet run -c Release --project tests/Coda.Tui.Benchmarks -- --filter *TranscriptScroll*
dotnet run -c Release --project tests/Coda.Tui.Benchmarks -- --filter *Streaming*
```

Fast CI smoke subset (executes every benchmark once via `Job.Dry`; validates the harness
builds and every path runs — timings are indicative only):

```
dotnet run -c Release --project tests/Coda.Tui.Benchmarks -- --smoke
```

> Always run in `-c Release`. A Debug build produces meaningless numbers; the project sets
> `Optimize=true` defensively but the SDK still needs the Release configuration for the
> referenced `Coda.Tui` assembly.

## What is measured

| Class | Workstream | Gate it informs |
| --- | --- | --- |
| `ComposerLayoutBenchmarks` | WS1 composer | Printable key at 10k-char draft < 5 ms p95; alloc < 100 KB/key. `LayoutPerKeystroke` models today's ~5× full-draft wrap that the Phase 1 cache should collapse to one. |
| `TranscriptFormatBenchmarks` | WS2 transcript | Per-block Markdig parse + wrap cost incurred when an evicted block scrolls back in. |
| `TranscriptScrollBenchmarks` | WS2 transcript | Scroll frame < 16 ms p95 with 10k blocks; separates full reflow from viewport-bounded scroll cost. |
| `StreamingBenchmarks` | WS3 streaming | Streaming UI frame < 16 ms p95; `StreamWholeReformat` (current O(L²)) vs `StreamTailOnlyOnce` (O(L) target) quantifies the prize. |

`[MemoryDiagnoser]` is enabled everywhere so allocation-per-operation targets are captured
alongside time.

## Not yet covered

Workstream 4 (`coda serve` transport: normal/slow/blocked reader, queue depth,
allocation-per-delta) is intentionally deferred and will be added when WS4 lands, since it
needs the bounded single-writer transport in place to benchmark meaningfully.
