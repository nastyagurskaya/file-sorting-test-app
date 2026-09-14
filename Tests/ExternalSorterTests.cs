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

    // Sort runs on a separate task with a time limit, so a hang fails the test with a TimeoutException
    // instead of blocking the whole test run.
    [Fact]
    public async Task MissingInputFileFailsInsteadOfHanging()
    {
        using TempWorkspace workspace = new();

        var sort = Task.Run(() => Sort(workspace));

        await Assert.ThrowsAsync<FileNotFoundException>(() => sort.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task LockedInputFileFailsInsteadOfHanging()
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input, ["1. Apple"]);
        using FileStream exclusiveLock = new(workspace.Input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var sort = Task.Run(() => Sort(workspace));

        await Assert.ThrowsAnyAsync<IOException>(() => sort.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ConcurrentSortsSharingATempFolderDoNotCollide()
    {
        using TempWorkspace first = new();
        using TempWorkspace second = new();
        Generate(first, sizeBytes: 1024 * 1024, seed: 11);
        Generate(second, sizeBytes: 1024 * 1024, seed: 12);
        var sharedTemp = first.Temp;

        await Task.WhenAll(
            Task.Run(() => new ExternalSorter(new SorterOptions(first.Input, first.Output, sharedTemp, 4 * 1024)).Sort()),
            Task.Run(() => new ExternalSorter(new SorterOptions(second.Input, second.Output, sharedTemp, 4 * 1024)).Sort()));

        AssertOrdered(first.Output);
        AssertSameLines(first.Input, first.Output);
        AssertOrdered(second.Output);
        AssertSameLines(second.Input, second.Output);
    }

    // A directory at the output path makes the final move fail after the merge has written the whole staging file.
    [Fact]
    public void FailedFinalMergeKeepsExistingOutputAndRemovesStagingFile()
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input, ["2. Banana", "1. Apple"]);
        Directory.CreateDirectory(workspace.Output);
        var previousOutput = Path.Combine(workspace.Output, "previous.txt");
        File.WriteAllText(previousOutput, "previous output");

        Assert.ThrowsAny<IOException>(() => Sort(workspace));

        Assert.Equal("previous output", File.ReadAllText(previousOutput));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(workspace.Output)!, "*.tmp"));
    }

    // Guarded by a time limit: without validation, zero workers can hang once the channel fills.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RejectsNonPositiveWorkerCount(int workers)
    {
        using TempWorkspace workspace = new();
        Generate(workspace, sizeBytes: 64 * 1024, seed: 1);
        var sorter = new ExternalSorter(new SorterOptions(workspace.Input, workspace.Output, workspace.Temp, 1024, workers));

        var sort = Task.Run(sorter.Sort);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sort.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void RejectsNonPositiveChunkSize(long chunkSizeBytes)
    {
        using TempWorkspace workspace = new();
        File.WriteAllLines(workspace.Input, ["2. Banana", "1. Apple"]);

        Assert.Throws<ArgumentOutOfRangeException>(() => Sort(workspace, chunkSizeBytes));
    }

    [Fact]
    public void SecondSortOnTheSameInstanceReportsOnlyItsOwnRun()
    {
        using TempWorkspace workspace = new();
        // A 1-byte chunk makes one run per line, and a merge factor of 2 forces intermediate passes on the first sort.
        var sorter = new ExternalSorter(new SorterOptions(workspace.Input, workspace.Output, workspace.Temp, 1, MergeFactor: 2));

        File.WriteAllLines(workspace.Input, ["5. E", "bad", "4. D", "3. C", "also bad", "2. B", "1. A"]);
        sorter.Sort();

        File.WriteAllLines(workspace.Input, ["9. Z", "still bad"]);
        var second = sorter.Sort();

        Assert.Equal(1L, second.LinesWritten);
        Assert.Equal(1L, second.LinesSkipped);
        Assert.Equal(1, second.RunCount);
        Assert.Equal(0, second.MergePasses);
        Assert.Equal(["9. Z"], File.ReadAllLines(workspace.Output));
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
