using Generator;
using Shared;
using Sorter;

namespace Tests;

public class ExternalSorterTests
{
    [Fact]
    public void OutputIsOrderedByTextThenNumber()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 200 * 1024, seed: 42);

        Sort(workspace, chunkSizeBytes: 16 * 1024);

        AssertOrdered(workspace.Output);
    }

    [Fact]
    public void OutputIsPermutationOfValidInput()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 200 * 1024, seed: 42);

        Sort(workspace, chunkSizeBytes: 16 * 1024);

        AssertSameLines(workspace.Input, workspace.Output);
    }

    [Fact]
    public void EmptyInputProducesEmptyOutput()
    {
        using TempWorkspace workspace = new();
        File.WriteAllText(workspace.Input, string.Empty);

        var result = Sort(workspace);

        Assert.Equal(0L, result.LinesWritten);
        Assert.Equal(0L, result.LinesSkipped);
        Assert.Equal(0, result.RunCount);
        Assert.Equal(string.Empty, File.ReadAllText(workspace.Output));
    }

    [Fact]
    public void SingleLinePassesThroughUnchanged()
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input, ["415. Apple"]);

        var result = Sort(workspace);

        Assert.Equal(1L, result.LinesWritten);
        Assert.Equal(["415. Apple"], File.ReadAllLines(workspace.Output));
    }

    [Fact]
    public void LinesSharingOneTextAreOrderedByNumber()
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input, ["100. Apple", "3. Apple", "20. Apple", "9. Apple", "1. Apple"]);

        Sort(workspace);

        Assert.Equal(
            ["1. Apple", "3. Apple", "9. Apple", "20. Apple", "100. Apple"],
            File.ReadAllLines(workspace.Output));
    }

    [Fact]
    public void ChunkSmallerThanInputProducesSeveralRunsAndStillSorts()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 200 * 1024, seed: 7);

        var result = Sort(workspace, chunkSizeBytes: 8 * 1024);

        Assert.True(result.RunCount > 1, $"Expected the input to be split into several runs, got {result.RunCount}.");
        AssertOrdered(workspace.Output);
        AssertSameLines(workspace.Input, workspace.Output);
        Assert.Equal(File.ReadLines(workspace.Input).Count(), result.LinesWritten);
    }

    [Fact]
    public void MalformedLinesAreSkippedCountedAndLeftOutOfOutput()
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input,
        [
            "415. Apple",
            "no separator here",
            "1. Banana",
            "99999999999999999999. Beyond long range",
            "abc. Not a number",
            "7. Cherry"
        ]);

        var result = Sort(workspace);

        Assert.Equal(3L, result.LinesWritten);
        Assert.Equal(3L, result.LinesSkipped);
        Assert.Equal(["415. Apple", "1. Banana", "7. Cherry"], File.ReadAllLines(workspace.Output));
    }

    // Before Number was a long, every line here above int.MaxValue was silently dropped as malformed.
    [Fact]
    public void NumbersBeyondIntRangeAreSortedAndKept()
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input,
        [
            "9223372036854775807. Apple",
            "2147483648. Apple",
            "5. Apple",
            "3000000000. Banana",
            "2147483647. Apple",
            "1. Banana"
        ]);

        // A 1-byte chunk puts every line in its own run, so large numbers go through run files and the merge.
        var result = Sort(workspace, chunkSizeBytes: 1);

        Assert.Equal(0L, result.LinesSkipped);
        Assert.Equal(
            ["5. Apple", "2147483647. Apple", "2147483648. Apple", "9223372036854775807. Apple", "1. Banana", "3000000000. Banana"],
            File.ReadAllLines(workspace.Output));
    }

    [Fact]
    public void RunFilesAreDeletedAfterSorting()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 64 * 1024, seed: 1);

        Sort(workspace, chunkSizeBytes: 8 * 1024);

        Assert.Empty(Directory.GetFiles(workspace.Temp));
    }

    [Fact]
    public void RunsFarAboveMergeFactorAreMergedInSeveralPasses()
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 64 * 1024, seed: 3);
        const int mergeFactor = 3;

        var result = Sort(workspace, chunkSizeBytes: 1024, mergeFactor);

        // More than factor² runs guarantees one pass cannot bring them down to the factor.
        Assert.True(result.RunCount > mergeFactor * mergeFactor, $"Precondition: expected more than {mergeFactor * mergeFactor} runs, got {result.RunCount}.");
        Assert.True(result.MergePasses >= 2, $"Expected several intermediate passes, got {result.MergePasses}.");
        AssertOrdered(workspace.Output);
        AssertSameLines(workspace.Input, workspace.Output);
        Assert.Empty(Directory.GetFiles(workspace.Temp));
    }

    // FileGenerator is only a source of realistic, varied input here — these tests assert on
    // ExternalSorter's behavior, not on what FileGenerator produces.
    static void Generate(TempWorkspace workspace, long sizeBytes, int seed) =>
        new FileGenerator(new GeneratorOptions(workspace.Input, sizeBytes, seed)).Generate();

    static SortResult Sort(TempWorkspace workspace, long chunkSizeBytes = 1024 * 1024, int mergeFactor = SorterOptions.DefaultMergeFactor) =>
        new ExternalSorter(new SorterOptions(workspace.Input, workspace.Output, workspace.Temp, chunkSizeBytes, MergeFactor: mergeFactor)).Sort();

    static void AssertOrdered(string path)
    {
        LineRecord? previous = null;

        foreach (var line in File.ReadLines(path))
        {
            Assert.True(LineRecord.TryParse(line, out var current), $"Output holds a malformed line: {line}");

            if (previous is LineRecord earlier)
            {
                Assert.True(
                    LineComparer.Instance.Compare(earlier, current) <= 0,
                    $"Out of order: '{earlier.Number}. {earlier.Text}' precedes '{line}'.");
            }

            previous = current;
        }
    }

    // Same multiset of lines, so nothing was lost, duplicated or invented; ordinal sorting of both
    // sides makes the comparison independent of the order under test.
    static void AssertSameLines(string inputPath, string outputPath)
    {
        List<string> expected = [.. File.ReadLines(inputPath)
            .Where(line => LineRecord.TryParse(line, out _))
            .Order(StringComparer.Ordinal)];

        List<string> actual = [.. File.ReadLines(outputPath).Order(StringComparer.Ordinal)];

        Assert.Equal(expected, actual);
    }
}
