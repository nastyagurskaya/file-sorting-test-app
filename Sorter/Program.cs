using Shared;
using Sorter;

const long DefaultChunkSize = 64L * 1024 * 1024;

// Benchmarked on a 10-core machine sorting 2GB: speedup plateaus at 4 workers (33.6s at 4 vs 34.9s
// at 10) while peak RSS keeps climbing (3.17GB at 4 vs 4.37GB at 10) — past 4, more workers cost
// memory without buying speed. --workers overrides this for anyone who wants more anyway.
const int DefaultMaxWorkers = 4;

string? input = null;
string? output = null;
string temp = Path.GetTempPath();
long chunkSize = DefaultChunkSize;
int workers = Math.Min(Environment.ProcessorCount, DefaultMaxWorkers);
bool valid = args.Length % 2 == 0;

for (int i = 0; valid && i < args.Length; i += 2)
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
        default:
            valid = false;
            break;
    }
}

if (!valid || input is null || output is null)
{
    Console.Error.WriteLine("Usage: Sorter --input <path> --output <path> [--temp <folder>] [--chunk-size <bytes|64MB|1GB>] [--workers <n>]");
    return 1;
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Input file not found: {input}");
    return 1;
}

SortResult result = new ExternalSorter(new SorterOptions(input, output, temp, chunkSize, workers)).Sort();
Console.WriteLine($"Sorted {result.LinesWritten:N0} lines from {result.RunCount:N0} runs to {output} on {workers} workers, skipped {result.LinesSkipped:N0} malformed lines.");
return 0;
