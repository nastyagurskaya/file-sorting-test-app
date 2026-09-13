using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Shared;

namespace Sorter;

record SorterOptions(
    string Input,
    string Output,
    string TempFolder,
    long ChunkSizeBytes,
    int? Workers = null,
    int MergeFactor = SorterOptions.DefaultMergeFactor)
{
    public const int DefaultMergeFactor = 64;
}

// RunCount is what phase 1 produced; MergePasses counts the intermediate passes before the final merge.
record SortResult(long LinesWritten, long LinesSkipped, int RunCount, int MergePasses);

sealed class ExternalSorter(SorterOptions options)
{
    const int BufferSize = 1024 * 1024;
    const int ChannelCapacity = 2;

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    long skipped;
    int mergePasses;
    int nextRunIndex;

    public SortResult Sort()
    {
        // A factor of 1 would merge each run into a copy of itself and never finish.
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MergeFactor, 2);

        Directory.CreateDirectory(options.TempFolder);
        List<string> runFiles = [];

        try
        {
            SplitAsync(runFiles).GetAwaiter().GetResult();
            var runCount = runFiles.Count;
            var lines = Merge(runFiles);

            return new SortResult(lines, skipped, runCount, mergePasses);
        }
        finally
        {
            DeleteRunFiles(runFiles);
        }
    }

    async Task SplitAsync(List<string> runFiles)
    {
        var workerCount = options.Workers ?? Environment.ProcessorCount;

        var channel =
            Channel.CreateBounded<(string Path, List<LineRecord> Records)>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleWriter = true
                });

        var freeBuffers = Channel.CreateUnbounded<List<LineRecord>>(
            new UnboundedChannelOptions { SingleReader = true });

        var workers = new Task[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var (path, records) in channel.Reader.ReadAllAsync())
                    {
                        WriteChunk(records, path);
                        records.Clear();
                        freeBuffers.Writer.TryWrite(records);
                    }
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                    throw;
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
                if (!LineRecord.TryParse(line, out var record))
                {
                    skipped++;
                    continue;
                }

                chunk.Add(record);
                chunkBytes += Encoding.UTF8.GetByteCount(line) + 1;

                if (chunkBytes >= options.ChunkSizeBytes)
                {
                    await HandOverAsync(writer, chunk, runFiles);
                    chunk = freeBuffers.TryRead(out var recycled) ? recycled : [];
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
            writer.Complete();
        }
    }

    async Task HandOverAsync(ChannelWriter<(string Path, List<LineRecord> Records)> writer, List<LineRecord> chunk, List<string> runFiles)
    {
        var path = NextRunPath();
        runFiles.Add(path);
        await writer.WriteAsync((path, chunk));
    }

    string NextRunPath() => Path.Combine(options.TempFolder, $"run-{nextRunIndex++:D5}.tmp");

    void WriteChunk(List<LineRecord> chunk, string path)
    {
        // Sorts the List's own backing array via a Span instead of Array.Sort on a ToArray() copy —
        // Array.Sort needs an array, but chunk never needs to grow again here, so no copy is required.
        CollectionsMarshal.AsSpan(chunk).Sort(LineComparer.Instance);

        using StreamWriter writer = new(path, append: false, Utf8NoBom, BufferSize);

        foreach (var record in chunk)
        {
            WriteRecord(writer, record);
        }
    }

    // Single-threaded on purpose: the merge is I/O-bound, and parallel reads on one disk contend.
    // Runs are merged in groups of MergeFactor until few enough remain for one final merge, so open
    // file handles and read buffers stay bounded however many runs phase 1 produced.
    long Merge(List<string> runFiles)
    {
        while (runFiles.Count > options.MergeFactor)
        {
            mergePasses++;
            string[] passInputs = [.. runFiles];

            for (var start = 0; start < passInputs.Length; start += options.MergeFactor)
            {
                var group = passInputs[start..Math.Min(start + options.MergeFactor, passInputs.Length)];
                var merged = NextRunPath();
                runFiles.Add(merged);

                MergeFiles(group, merged);

                // Delete inputs as soon as they are merged: keeps temp at ~1x the data, and keeps
                // runFiles equal to what is on disk for Sort's cleanup.
                foreach (var input in group)
                {
                    File.Delete(input);
                    runFiles.Remove(input);
                }
            }
        }

        return MergeFiles(runFiles, options.Output);
    }

    static long MergeFiles(IReadOnlyList<string> inputs, string outputPath)
    {
        List<StreamReader> readers = new(inputs.Count);
        PriorityQueue<(LineRecord Record, int Run), LineRecord> queue = new(inputs.Count, LineComparer.Instance);

        try
        {
            using StreamWriter writer = new(outputPath, append: false, Utf8NoBom, BufferSize);

            for (var i = 0; i < inputs.Count; i++)
            {
                readers.Add(new StreamReader(inputs[i], Utf8NoBom, detectEncodingFromByteOrderMarks: false, BufferSize));

                if (TryReadRecord(readers[i], inputs[i], out var record))
                {
                    queue.Enqueue((record, i), record);
                }
            }

            long lines = 0;

            while (queue.TryDequeue(out var item, out _))
            {
                WriteRecord(writer, item.Record);
                lines++;

                if (TryReadRecord(readers[item.Run], inputs[item.Run], out var next))
                {
                    queue.Enqueue((next, item.Run), next);
                }
            }

            return lines;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    static bool TryReadRecord(StreamReader reader, string path, out LineRecord record)
    {
        var line = reader.ReadLine();

        if (line is null)
        {
            record = default;
            return false;
        }

        if (!LineRecord.TryParse(line, out record))
        {
            throw new InvalidDataException($"Corrupt run file '{path}', cannot parse line: {line}");
        }

        return true;
    }

    // No string per record: at hundreds of millions of records that is the dominant allocation.
    // 11 chars fit any int, so TryFormat cannot run out of room.
    static void WriteRecord(StreamWriter writer, LineRecord record)
    {
        Span<char> digits = stackalloc char[11];
        record.Number.TryFormat(digits, out var written, provider: CultureInfo.InvariantCulture);

        writer.Write(digits[..written]);
        writer.Write(LineRecord.Separator);
        writer.Write(record.Text);
        writer.Write('\n');
    }

    static void DeleteRunFiles(List<string> runFiles)
    {
        foreach (var runFile in runFiles)
        {
            try
            {
                File.Delete(runFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not delete run file '{runFile}': {exception.Message}");
            }
        }
    }
}
