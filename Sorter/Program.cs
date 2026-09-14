using Shared;
using Sorter;

const long DefaultChunkSize = 64L * 1024 * 1024;
const int DefaultMaxWorkers = 4;

string? input = null;
string? output = null;
var temp = Path.GetTempPath();
var chunkSize = DefaultChunkSize;
var workers = Math.Min(Environment.ProcessorCount, DefaultMaxWorkers);
var mergeFactor = SorterOptions.DefaultMergeFactor;
var valid = args.Length % 2 == 0;

for (var i = 0; valid && i < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--input":
            input = args[i + 1];
            break;
        case "--output":
            output = args[i + 1];
            break;
        case "--temp":
            temp = args[i + 1];
            break;
        case "--chunk-size":
            valid = SizeParser.TryParse(args[i + 1], out chunkSize);
            break;
        case "--workers":
            valid = int.TryParse(args[i + 1], out workers) && workers > 0;
            break;
        case "--merge-factor":
            valid = int.TryParse(args[i + 1], out mergeFactor) && mergeFactor >= 2;
            break;
        default:
            valid = false;
            break;
    }
}

if (!valid || input is null || output is null)
{
    Console.Error.WriteLine("Usage: Sorter --input <path> --output <path> [--temp <folder>] [--chunk-size <bytes|64MB|1GB>] [--workers <n>] [--merge-factor <n>]");
    return 1;
}

SortResult result;

// File-system problems (missing, locked or unwritable files) get a short message; anything else is a bug
// and keeps its stack trace.
try
{
    result = new ExternalSorter(new SorterOptions(input, output, temp, chunkSize, workers, mergeFactor)).Sort();
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Sort failed: {exception.Message}");
    return 1;
}
Console.WriteLine($"Sorted {result.LinesWritten:N0} lines from {result.RunCount:N0} runs ({result.MergePasses} intermediate merge passes) to {output} on {workers} workers.");

if (result.LinesSkipped > 0)
{
    Console.WriteLine($"Skipped {result.LinesSkipped:N0} malformed lines");
}

return 0;
