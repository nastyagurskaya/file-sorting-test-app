# External sort

Two .NET 10 console programs:

- **Generator** writes a file of lines `<Number>. <String>`, e.g. `415. Apple`, with many lines sharing the same `String`.
- **Sorter** sorts such a file by `String` (ordinal), then by `Number` ascending. It streams everything, so the input can be far larger than RAM (~100 GB).

## Build & run

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
dotnet test
```

Generate a file. `--size` takes bytes or a `KB`/`MB`/`GB` suffix; `--seed` is optional and makes the output reproducible.

```bash
dotnet run -c Release --project Generator -- --output data.txt --size 1GB --seed 42
```

Sort it:

```bash
dotnet run -c Release --project Sorter -- --input data.txt --output sorted.txt
```

| Sorter option | Default | Meaning |
|---|---|---|
| `--input`, `--output` | required | Input and output file paths |
| `--temp` | system temp folder | Where run files are written |
| `--chunk-size` | `64MB` | Input bytes per sorted run |
| `--workers` | `min(cores, 4)` | Threads sorting chunks in phase 1 |
| `--merge-factor` | `64` | Maximum runs merged at once |

The Sorter prints line count, run count, merge passes and worker count. If any lines were malformed, it also prints how many were skipped.

## Algorithm

External merge sort, in two phases.

1. **Split.** Read the input sequentially, parse each line, and collect records until a chunk reaches `--chunk-size` bytes. Sort the chunk in memory and write it to a temporary *run* file.
2. **Merge.** Open the runs and do a k-way merge through a priority queue holding one record per run. Repeatedly write the smallest record and replace it with the next record from the same run. If there are more runs than `--merge-factor`, first merge them in groups into intermediate runs, repeating until at most `--merge-factor` remain, then do the final merge into the output.

Run files are deleted when the sort finishes, including when it fails. They are named `run-<tag>-NNNNN.tmp` in `--temp`: the tag is unique per sort, which makes concurrent sorts sharing a folder safe, and the index only grows, so intermediate merge passes never reuse the name of a run that hasn't been merged yet.

## Design decisions

**`long` for Number.** The brief doesn't bound the number's range, and the sorter has to handle input it didn't produce. A value beyond `long` fails `long.TryParse` and is counted as malformed rather than silently mis-sorted — and it costs nothing, since the struct is 16 bytes either way.

**Split on the first `". "` only.** The `String` part may contain dots and digits itself (`32. Cherry is the best`), so everything after the first separator is text.

**Output is written from parsed records.** Lines are written back from `(Number, Text)` rather than copied byte for byte, so numbers are normalised: `0001. Apple` comes out as `1. Apple`. Keeping the original spelling would mean holding an extra string per record in memory.

**`StringComparer.Ordinal`.** Deterministic, culture-independent, and allocation-free. `ToLower()` would allocate a string on every comparison; culture-aware comparison would change results between machines. Order is case-sensitive: `Apple` < `Banana` < `apple`.

**Minimal structure.** Four projects, concrete classes, no interfaces/factories/DI of our own — every piece has exactly one implementation, so an abstraction layer would add indirection without flexibility. `Shared` holds the line format, the comparer, and the size parser both programs use.

**Byte-bounded chunks.** Line lengths vary, so counting input bytes (not line count) gives every chunk the same memory budget.

**Only phase 1 is parallel.** Sorting chunks is CPU-bound and independent per chunk, so N workers sort behind a bounded `Channel` (capacity 2) fed by one reader; workers hand emptied buffers back for reuse. Phase 2 stays sequential — one ordered stream, and parallel reads on one disk would contend. That's a design choice, not a measured one: these benchmarks run on a warm file cache and don't show how much of the merge is spent waiting on I/O.

**Multi-pass merge.** Each open run costs a file handle and ~190 KB of buffers, so merging all runs at once at 100 GB scale (~1,600 runs) would exceed typical file-descriptor limits. Merging in groups of `--merge-factor` (default 64) bounds that, at the cost of one intermediate pass; each group's inputs are deleted as soon as it's merged, so temp space stays around 1× the data.

**Memory model.** Peak phase-1 memory is roughly `chunkSize × (workers + 3) × 3` — the ×3 is a chunk's in-memory cost versus its size on disk (UTF-16 strings, object headers, record array). Phase 2 needs about `mergeFactor × 190 KB` for the readers plus one writer buffer; measured peak RSS was ~90 MB for a 200 MB file split into 800 runs.

**Default of 4 workers.** Measured: 4→10 workers saves ~6% more time for a full extra chunk of memory per worker. Past 4, the bottleneck is the single reader/parser and the single-threaded merge.

**Malformed lines.** Skipped, counted, and left out of the output; the total is printed at the end. Not logged one by one — synchronised console writes would dominate the run time on junk-heavy input. A malformed line in our own run files is treated as corruption (the merge throws), since we wrote those ourselves from already-parsed records.

**Atomic output replace.** The final merge writes to a staging file beside the output and moves it into place only on success, so a failed merge never leaves a truncated output file.

**Argument validation.** `Workers`, `ChunkSizeBytes`, and `MergeFactor` are checked at the start of `Sort()` — a non-positive value would otherwise hang (workers) or silently misbehave (chunk size) instead of failing clearly.

**Not taken: adaptive grouping with a binary run format.** An external review proposed this, with a measured 2.59× speedup. It adds a second file format to keep correct and three heuristic thresholds — complexity without matching engineering quality, so run files stay plain text in the same format as the input.

## Timing: 1 worker vs N workers

A 1 GB file (50.7M lines, seed 42) sorted with default settings except `--workers`. Each configuration was run three times on a warm OS file cache; the table shows the median.

Machine: Apple M4 (10 cores), 24 GB RAM, SSD, macOS 15.6, .NET 10, Release build.

| Workers | Time | Speedup |
|---|---|---|
| 1 | 23.2 s | 1.00× |
| 2 | 17.7 s | 1.31× |
| 4 | 14.9 s | 1.55× |
| 10 | 14.4 s | 1.62× |

The output file was byte-identical for every worker count. Speedup flattens quickly because phase 1 still has a serial part (reading and parsing on one thread) and phase 2 is serial by design.

## Edge cases and tests

The tests assert behaviour, not existence. The sorter tests don't set a worker count, so the sorter uses one worker per core and the tests exercise the parallel path.

Core properties, checked on generated data split into several runs:

- Output is ordered by `(Text, Number)`: `OutputIsOrderedByTextThenNumber`.
- Output is a permutation of the valid input lines, with nothing lost, duplicated or invented: `OutputIsPermutationOfValidInput`.

| Edge case | Test |
|---|---|
| Empty input file | `EmptyInputProducesEmptyOutput` |
| Single line | `SingleLinePassesThroughUnchanged` |
| All lines share one `String` (Number tie-break) | `LinesSharingOneTextAreOrderedByNumber`, `BreaksTiesByNumberAscending` |
| Numbers compared numerically, not as text (`9` before `100`) | `SortsNumbersNumericallyNotLexicographically` |
| Chunk smaller than the file (several runs, real merge) | `ChunkSmallerThanInputProducesSeveralRunsAndStillSorts` |
| Far more runs than the merge factor (several passes) | `RunsFarAboveMergeFactorAreMergedInSeveralPasses` |
| Malformed lines skipped, counted, not written | `MalformedLinesAreSkippedCountedAndLeftOutOfOutput`, `ReportsMalformedLineAsFailure` |
| `String` containing `". "` and digits | `SplitsOnFirstSeparatorOnly`, `KeepsLaterSeparatorsInsideText` |
| Number at `long.MaxValue` / beyond `long` range | `ParsesNumberAtLongMaxValue`, `ReportsNumberBeyondLongRangeAsFailure` |
| Numbers above `int.MaxValue` survive run files and the merge | `NumbersBeyondIntRangeAreSortedAndKept` |
| Case sensitivity of the ordering | `ComparesTextCaseSensitively` |
| Example from the task brief | `SortsTheBriefsExampleIntoExpectedOrder` |
| Run files removed after the sort | `RunFilesAreDeletedAfterSorting` |

The generator tests check the properties the sorter relies on: every line is well-formed (`EveryGeneratedLineIsWellFormed`), many lines share a `String` (`ManyLinesShareTheSameText`), numbers cover every length up to 19 digits (`ProducesNumbersOfEveryLengthUpToLong`), the pool includes text containing the separator and case-only variants, the same seed gives identical output, and output has `\n` line endings with no BOM.

## AI assistance

Claude Code was used for implementation and to run the benchmarks in this README. The design decisions, the review of what it produced, and verification of the results are mine.
