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

The Sorter prints line count, run count, merge passes, worker count and elapsed time. If any lines were malformed, it also prints how many were skipped.

## Algorithm

External merge sort, in two phases.

1. **Split.** Read the input sequentially, parse each line, and collect records until a chunk reaches `--chunk-size` bytes. Sort the chunk in memory and write it to a temporary *run* file.
2. **Merge.** Open the runs and do a k-way merge through a priority queue holding one record per run. Repeatedly write the smallest record and replace it with the next record from the same run. If there are more runs than `--merge-factor`, first merge them in groups into intermediate runs, repeating until at most `--merge-factor` remain, then do the final merge into the output.

Run files are deleted when the sort finishes, including when it fails.

## Design decisions

**`int` for Number.** The data comes from our own generator (numbers 1–99 999), so there is no unbounded external range to guard against. A number outside `int` range fails `int.TryParse` and is handled as a malformed line, not a crash.

**Split on the first `". "` only.** The `String` part may contain dots and digits itself (`32. Cherry is the best`), so everything after the first separator is text.

**`StringComparer.Ordinal`.** Deterministic, independent of the machine's culture, and allocation-free. Case-insensitive comparison via `ToLower()` would allocate a string on every comparison — billions of allocations on a 100 GB sort — and culture-aware comparison would change results between machines. As a consequence the order is case-sensitive: `Apple` < `Banana` < `apple`.

**Minimal structure.** Four projects (`Generator`, `Sorter`, `Shared`, `Tests`), concrete classes, no interfaces, factories or DI. Every piece has exactly one implementation, so an abstraction layer would add indirection without adding flexibility. `Shared` holds only the line format (`LineRecord`) and the ordering (`LineComparer`), so both programs agree on it.

**Byte-bounded chunks.** Line lengths vary, so a fixed line count would give unpredictable memory use. Counting input bytes gives the same budget for every chunk regardless of what the lines look like.

**Only phase 1 is parallel.** Parsing and sorting chunks is CPU-bound and each chunk is independent. One reader feeds N workers through a bounded `Channel` (capacity 2), so a fast reader waits for the workers instead of piling chunks up in memory. Workers hand emptied chunk buffers back to the reader for reuse. Phase 2 stays single-threaded: it is one ordered stream, and parallel reads from the same disk compete for I/O rather than speeding it up.

**Multi-pass merge.** Each open run costs a file handle and a ~1 MB read buffer. 100 GB in 64 MB chunks is ~1 600 runs, which would exceed common file-descriptor limits (256 on macOS by default) and hold ~1.6 GB of buffers. Merging in groups of 64 bounds both and costs one extra pass over the data at that size. Each group's inputs are deleted as soon as they are merged, so temp space stays around 1× the data.

**Memory model.** Peak memory in phase 1 is roughly `chunkSize × (workers + 3)`: one chunk being read, up to two waiting in the channel, and one per worker. A chunk's in-memory cost is about 3× its size in the file (UTF-16 strings, object headers, record array); this was measured at ~725 MB of live data for four 64 MB chunks. So `chunkSize × (workers + 3) × 3` has to fit in RAM. Process RSS runs higher than that, because the GC collects lazily when it is not under memory pressure. Phase 2 needs about `mergeFactor × 1 MB`.

**Default of 4 workers.** In the measurement below, going from 4 to 10 workers saves ~6% of the time, while each extra worker adds a chunk's worth of memory. The bottleneck past that point is the single reader, which parses every line, and the single-threaded merge.

**Malformed lines.** A line without `". "` or with an unparseable number is skipped, counted, and left out of the output. The total is printed at the end so a shorter output file is explained. Lines are not logged one by one: on a junk-heavy file the synchronised console writes would dominate the run time. Run files are different: we write them ourselves from already parsed records, so an unparseable line there means corruption, and the merge throws instead of silently dropping the rest of that run.

## Timing: 1 worker vs N workers

A 1 GB file (50.9M lines, seed 42) sorted with default settings except `--workers`. Elapsed time is measured with a `Stopwatch` around `Sort()`. Each configuration was run three times on a warm OS file cache; the table shows the median.

Machine: Apple M4 (10 cores), 24 GB RAM, SSD, macOS 15.6, .NET 10, Release build.

| Workers | Time | Speedup |
|---|---|---|
| 1 | 24.3 s | 1.00× |
| 2 | 17.5 s | 1.39× |
| 4 | 15.6 s | 1.56× |
| 10 | 14.7 s | 1.65× |

The output file was byte-identical for every worker count. Speedup flattens quickly because phase 1 still has a serial part (reading and parsing on one thread) and phase 2 is serial by design.

## Edge cases and tests

The tests assert behaviour, not existence. The sorter tests run with the default worker count, so they exercise the parallel path.

Core properties, checked on generated data split into several runs:

- Output is ordered by `(Text, Number)` — `OutputIsOrderedByTextThenNumber`.
- Output is a permutation of the valid input lines, with nothing lost, duplicated or invented — `OutputIsPermutationOfValidInput`.

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
| Number at `int.MaxValue` / beyond `int` range | `ParsesNumberAtIntMaxValue`, `ReportsNumberBeyondIntRangeAsFailure` |
| Case sensitivity of the ordering | `ComparesTextCaseSensitively` |
| Example from the task brief | `SortsTheBriefsExampleIntoExpectedOrder` |
| Run files removed after the sort | `RunFilesAreDeletedAfterSorting` |

The generator tests check the properties the sorter relies on: every line is well formed (`EveryGeneratedLineIsWellFormed`), many lines share a `String` (`ManyLinesShareTheSameText`), the pool includes text containing the separator and case-only variants, the same seed gives identical output, and output has `\n` line endings with no BOM.

Not covered by a test: the exception on a corrupt run file. Run files are created and deleted inside `Sort()`, and adding a hook only to corrupt one would be an abstraction that exists solely for the test.
