using System.Text;
using Shared;

namespace Sorter;

record SorterOptions(string Input, string Output, string TempFolder, long ChunkSizeBytes);

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
            Split(runFiles);
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

    void Split(List<string> runFiles)
    {
        using StreamReader reader = new(options.Input, Utf8NoBom, detectEncodingFromByteOrderMarks: true, BufferSize);

        List<LineRecord> chunk = [];
        long chunkBytes = 0;
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
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
                runFiles.Add(WriteChunk(chunk, runFiles.Count));
                chunk.Clear();
                chunkBytes = 0;
            }
        }

        if (chunk.Count > 0)
        {
            runFiles.Add(WriteChunk(chunk, runFiles.Count));
        }
    }

    string WriteChunk(List<LineRecord> chunk, int index)
    {
        LineRecord[] records = [.. chunk];
        Array.Sort(records, LineComparer.Instance);

        string path = Path.Combine(options.TempFolder, $"run-{index:D5}.tmp");
        using StreamWriter writer = new(path, append: false, Utf8NoBom, BufferSize) { NewLine = "\n" };

        foreach (var record in records)
        {
            writer.WriteLine(record.ToString());
        }

        return path;
    }

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