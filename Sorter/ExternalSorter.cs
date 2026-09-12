using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Shared;

namespace Sorter;

record SorterOptions(string Input, string Output, string TempFolder, long ChunkSizeBytes, int? Workers = null);

// RunCount is how many sorted temp files phase 1 (Split) produced and phase 2 (Merge) merged.
record SortResult(long LinesWritten, long LinesSkipped, int RunCount);

sealed class ExternalSorter(SorterOptions options)
{
    const int BufferSize = 1024 * 1024;

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    long skipped;

    public SortResult Sort()
    {
        Directory.CreateDirectory(options.TempFolder);
        List<string> runFiles = [];

        try
        {
            SplitAsync(runFiles).GetAwaiter().GetResult();
            long lines = Merge(runFiles);
            return new SortResult(lines, skipped, runFiles.Count);
        }
        finally
        {
            foreach (string runFile in runFiles)
            {
                File.Delete(runFile);
            }
        }
    }

    // Channel capacity is deliberately small: it exists to smooth out uneven sort times between
    // workers, not to buffer a chunk per worker. A bigger capacity just holds more chunks in
    // flight without speeding anything up.
    const int ChannelCapacity = 2;

    // Phase 1 is CPU-bound — parsing, sorting and formatting dominate — so chunks are sorted and
    // written by several workers at once.
    // Memory ceiling: chunkSize × (workers + ChannelCapacity) must fit in RAM, and a chunk costs
    // several times its byte budget once it is a list of records and strings.
    async Task SplitAsync(List<string> runFiles)
    {
        int workerCount = options.Workers ?? Environment.ProcessorCount;

        var channel =
            Channel.CreateBounded<(string Path, List<LineRecord> Records)>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    // Wait, not drop: a full channel must slow the reader down, never lose a chunk.
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true
                });

        // Workers hand emptied buffers back here so the reader reuses them instead of allocating a
        // fresh List per chunk — the single-threaded version reused one buffer via Clear(), and this
        // keeps that behavior under concurrency instead of regressing peak memory to grow it back.
        var freeBuffers = Channel.CreateUnbounded<List<LineRecord>>(
            new UnboundedChannelOptions { SingleReader = true });

        Task[] workers = new Task[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await foreach ((string path, List<LineRecord> records) in channel.Reader.ReadAllAsync())
                {
                    WriteChunk(records, path);
                    records.Clear();
                    freeBuffers.Writer.TryWrite(records);
                }
            });
        }

        try
        {
            await ReadChunksAsync(channel.Writer, freeBuffers.Reader, runFiles);
        }
        finally
        {
            await Task.WhenAll(workers);
        }
    }

    async Task ReadChunksAsync(
        ChannelWriter<(string Path, List<LineRecord> Records)> writer,
        ChannelReader<List<LineRecord>> freeBuffers,
        List<string> runFiles)
    {
        using StreamReader reader = new(options.Input, Utf8NoBom, detectEncodingFromByteOrderMarks: true, BufferSize);

        List<LineRecord> chunk = [];
        long chunkBytes = 0;
        string? line;

        try
        {
            while ((line = reader.ReadLine()) is not null)
            {
                // Parsing stays on the reader so the skipped count and the stderr log keep input order.
                if (!LineRecord.TryParse(line, out LineRecord record))
                {
                    Console.Error.WriteLine($"Skipped malformed line: {line}");
                    skipped++;
                    continue;
                }

                chunk.Add(record);

                // The budget counts file bytes, not managed memory: the record array and its strings take
                // several times this, so the default is set well below available RAM.
                chunkBytes += Encoding.UTF8.GetByteCount(line) + 1;

                if (chunkBytes >= options.ChunkSizeBytes)
                {
                    await HandOverAsync(writer, chunk, runFiles);
                    chunk = freeBuffers.TryRead(out List<LineRecord>? recycled) ? recycled : [];
                    chunkBytes = 0;
                }
            }

            if (chunk.Count > 0)
            {
                await HandOverAsync(writer, chunk, runFiles);
            }
        }
        finally
        {
            // Without this the workers would wait on an open channel forever if reading threw.
            writer.Complete();
        }
    }

    // The reader names the file and records it before handing the chunk over, so a worker that dies
    // mid-write still leaves the path for Sort's cleanup. Single-threaded, so no locking is needed.
    async Task HandOverAsync(ChannelWriter<(string Path, List<LineRecord> Records)> writer, List<LineRecord> chunk, List<string> runFiles)
    {
        string path = Path.Combine(options.TempFolder, $"run-{runFiles.Count:D5}.tmp");
        runFiles.Add(path);
        await writer.WriteAsync((path, chunk));
    }

    // Sorts the List's own backing array via a Span instead of Array.Sort on a ToArray() copy —
    // Array.Sort needs an array, but chunk never needs to grow again here, so no copy is required.
    void WriteChunk(List<LineRecord> chunk, string path)
    {
        CollectionsMarshal.AsSpan(chunk).Sort(LineComparer.Instance);

        using StreamWriter writer = new(path, append: false, Utf8NoBom, BufferSize) { NewLine = "\n" };

        foreach (var record in chunk)
        {
            writer.WriteLine(record.ToString());
        }
    }

    // Single-threaded on purpose: the merge is I/O-bound, and parallel reads on one disk contend.
    long Merge(List<string> runFiles)
    {
        var readers = new StreamReader?[runFiles.Count];
        PriorityQueue<(LineRecord Record, int Run), LineRecord> queue = new(runFiles.Count, LineComparer.Instance);

        try
        {
            using StreamWriter writer = new(options.Output, append: false, Utf8NoBom, BufferSize) { NewLine = "\n" };

            for (int i = 0; i < runFiles.Count; i++)
            {
                readers[i] = new StreamReader(runFiles[i], Utf8NoBom, detectEncodingFromByteOrderMarks: false, BufferSize);

                if (TryReadRecord(readers[i], out LineRecord record))
                {
                    queue.Enqueue((record, i), record);
                }
            }

            long lines = 0;

            while (queue.TryDequeue(out (LineRecord Record, int Run) item, out _))
            {
                writer.WriteLine(item.Record.ToString());
                lines++;

                if (TryReadRecord(readers[item.Run], out LineRecord next))
                {
                    queue.Enqueue((next, item.Run), next);
                }
            }

            return lines;
        }
        finally
        {
            foreach (StreamReader? reader in readers)
            {
                reader?.Dispose();
            }
        }
    }

    static bool TryReadRecord(StreamReader? reader, out LineRecord record)
    {
        record = default;
        string? line = reader?.ReadLine();
        return line is not null && LineRecord.TryParse(line, out record);
    }
}
