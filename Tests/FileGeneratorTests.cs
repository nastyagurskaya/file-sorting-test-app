using System.Text;
using Generator;
using Shared;

namespace Tests;

public class FileGeneratorTests
{
    [Fact]
    public void EveryGeneratedLineIsWellFormed()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 64 * 1024, seed: 42);

        foreach (string line in File.ReadLines(workspace.Output))
        {
            Assert.True(LineRecord.TryParse(line, out _), $"Generator produced a malformed line: {line}");
        }
    }

    [Fact]
    public void ReportedCountsMatchTheFileOnDisk()
    {
        using TempWorkspace workspace = new();
        GenerationResult result = Generate(workspace, sizeBytes: 64 * 1024, seed: 42);

        Assert.Equal(File.ReadLines(workspace.Output).Count(), result.LinesWritten);
        Assert.Equal(new FileInfo(workspace.Output).Length, result.BytesWritten);
    }

    [Fact]
    public void StopsAtTheFirstLineThatReachesTheTargetSize()
    {
        using TempWorkspace workspace = new();
        const long target = 64 * 1024;

        GenerationResult result = Generate(workspace, target, seed: 42);

        string[] lines = File.ReadAllLines(workspace.Output);
        long withoutLastLine = result.BytesWritten - Encoding.UTF8.GetByteCount(lines[^1]) - 1;

        Assert.True(result.BytesWritten >= target, $"Wrote {result.BytesWritten} bytes, short of the {target} target.");
        Assert.True(withoutLastLine < target, $"Overshot by more than one line: {withoutLastLine} bytes already reach {target}.");
    }

    [Fact]
    public void SameSeedProducesIdenticalOutput()
    {
        using TempWorkspace first = new();
        using TempWorkspace second = new();

        Generate(first, sizeBytes: 32 * 1024, seed: 7);
        Generate(second, sizeBytes: 32 * 1024, seed: 7);

        Assert.Equal(File.ReadAllBytes(first.Output), File.ReadAllBytes(second.Output));
    }

    [Fact]
    public void DifferentSeedsProduceDifferentOutput()
    {
        using TempWorkspace first = new();
        using TempWorkspace second = new();

        Generate(first, sizeBytes: 32 * 1024, seed: 7);
        Generate(second, sizeBytes: 32 * 1024, seed: 8);

        Assert.NotEqual(File.ReadAllBytes(first.Output), File.ReadAllBytes(second.Output));
    }

    [Fact]
    public void OmittedSeedProducesDifferentOutputOnEachRun()
    {
        using TempWorkspace first = new();
        using TempWorkspace second = new();

        Generate(first, sizeBytes: 32 * 1024, seed: null);
        Generate(second, sizeBytes: 32 * 1024, seed: null);

        Assert.NotEqual(File.ReadAllBytes(first.Output), File.ReadAllBytes(second.Output));
    }

    [Fact]
    public void ManyLinesShareTheSameText()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 512 * 1024, seed: 42);

        List<LineRecord> records = ReadRecords(workspace);
        int distinctTexts = records.Select(record => record.Text).Distinct(StringComparer.Ordinal).Count();
        int mostRepeated = records.GroupBy(record => record.Text, StringComparer.Ordinal).Max(group => group.Count());

        Assert.True(distinctTexts * 5 < records.Count, $"Only {distinctTexts} distinct texts across {records.Count} lines is not enough reuse.");
        Assert.True(mostRepeated >= 5, $"The most common text appears just {mostRepeated} times.");
    }

    [Fact]
    public void NumbersStayInTheSmallRealisticRange()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 64 * 1024, seed: 42);

        foreach (LineRecord record in ReadRecords(workspace))
        {
            Assert.InRange(record.Number, 1, 99_999);
        }
    }

    // The pool deliberately holds values such as "32. Cherry is the best" so generated data
    // exercises the parser's first-separator-only rule.
    [Fact]
    public void ProducesTextsThatThemselvesContainTheSeparator()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 512 * 1024, seed: 42);

        Assert.Contains(ReadRecords(workspace), record => record.Text.Contains(LineRecord.Separator, StringComparison.Ordinal));
    }

    // Case variants make the ordinal, case-sensitive comparison observable in generated data.
    [Fact]
    public void ProducesTextsThatDifferOnlyByCase()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 512 * 1024, seed: 42);

        HashSet<string> texts = [.. ReadRecords(workspace).Select(record => record.Text)];

        Assert.Contains(texts, text => text != text.ToUpperInvariant() && texts.Contains(text.ToUpperInvariant()));
    }

    [Fact]
    public void WritesUnixLineEndingsWithoutBom()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 32 * 1024, seed: 42);

        byte[] bytes = File.ReadAllBytes(workspace.Output);

        Assert.False(bytes.Take(3).SequenceEqual<byte>([0xEF, 0xBB, 0xBF]), "Output starts with a UTF-8 BOM.");
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    static GenerationResult Generate(TempWorkspace workspace, long sizeBytes, int? seed) =>
        new FileGenerator(new GeneratorOptions(workspace.Output, sizeBytes, seed)).Generate();

    static List<LineRecord> ReadRecords(TempWorkspace workspace)
    {
        List<LineRecord> records = [];

        foreach (string line in File.ReadLines(workspace.Output))
        {
            Assert.True(LineRecord.TryParse(line, out LineRecord record), $"Generator produced a malformed line: {line}");
            records.Add(record);
        }

        return records;
    }
}
