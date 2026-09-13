using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Shared;

namespace Sorter;

record SorterOptions(string Input, string Output, string TempFolder, long ChunkSizeBytes, int? Workers = null);

record SortResult(long LinesWritten, long LinesSkipped, int RunCount);

sealed class ExternalSorter(SorterOptions options)
{
    const int BufferSize = 1024 * 1024;
    const int ChannelCapacity = 2;

    long skipped;

    static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public SortResult Sort()
    {
        Directory.CreateDirectory(options.TempFolder);
        List<string> runFiles = [];

        try
        {
            SplitAsync(runFiles).GetAwaiter().GetResult();
            var lines = Merge(runFiles);
            return new SortResult(lines, skipped, runFiles.Count);
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

        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                try
                {
                    await foreach ((string path, List<LineRecord> records) in channel.Reader.ReadAllAsync())
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
                if (!LineRecord.TryParse(line, out LineRecord record))
                {
                    skipped++;
                    continue;
                }

                chunk.Add(record);
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

    async Task HandOverAsync(ChannelWriter<(string Path, List<LineRecord> Records)> writer, List<LineRecord> chunk, List<string> runFiles)
    {
        string path = Path.Combine(options.TempFolder, $"run-{runFiles.Count:D5}.tmp");
        runFiles.Add(path);
        await writer.WriteAsync((path, chunk));
    }

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
    long Merge(List<string> runFiles)
    {
        List<StreamReader> readers = new(runFiles.Count);
        PriorityQueue<(LineRecord Record, int Run), LineRecord> queue = new(runFiles.Count, LineComparer.Instance);

        try
        {
            using StreamWriter writer = new(options.Output, append: false, Utf8NoBom, BufferSize);

            for (int i = 0; i < runFiles.Count; i++)
            {
                readers.Add(new StreamReader(runFiles[i], Utf8NoBom, detectEncodingFromByteOrderMarks: false, BufferSize));

                if (TryReadRecord(readers[i], runFiles[i], out LineRecord record))
                {
                    queue.Enqueue((record, i), record);
                }
            }

            long lines = 0;

            while (queue.TryDequeue(out (LineRecord Record, int Run) item, out _))
            {
                WriteRecord(writer, item.Record);
                lines++;

                if (TryReadRecord(readers[item.Run], runFiles[item.Run], out LineRecord next))
                {
                    queue.Enqueue((next, item.Run), next);
                }
            }

            return lines;
        }
        finally
        {
            foreach (StreamReader reader in readers)
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
    // 11 chars holds every int, and '\n' is written explicitly so output matches on every platform.
    static void WriteRecord(StreamWriter writer, LineRecord record)
    {
        Span<char> digits = stackalloc char[11];
        record.Number.TryFormat(digits, out int written, provider: CultureInfo.InvariantCulture);

        writer.Write(digits[..written]);
        writer.Write(LineRecord.Separator);
        writer.Write(record.Text);
        writer.Write('\n');
    }

    static void DeleteRunFiles(List<string> runFiles)
    {
        foreach (string runFile in runFiles)
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
