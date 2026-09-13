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

        SortResult result = Sort(workspace);

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

        SortResult result = Sort(workspace);

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

        SortResult result = Sort(workspace, chunkSizeBytes: 8 * 1024);

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
            "99999999999999999999. Beyond int range",
            "abc. Not a number",
            "7. Cherry"
        ]);

        SortResult result = Sort(workspace);

        Assert.Equal(3L, result.LinesWritten);
        Assert.Equal(3L, result.LinesSkipped);
        Assert.Equal(["415. Apple", "1. Banana", "7. Cherry"], File.ReadAllLines(workspace.Output));
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

        SortResult result = Sort(workspace, chunkSizeBytes: 1024, mergeFactor);

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

        foreach (string line in File.ReadLines(path))
        {
            Assert.True(LineRecord.TryParse(line, out LineRecord current), $"Output holds a malformed line: {line}");

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
