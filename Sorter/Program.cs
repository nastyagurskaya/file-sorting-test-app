using Shared;
using Sorter;

const long DefaultChunkSize = 64L * 1024 * 1024;

string? input = null;
string? output = null;
string temp = Path.GetTempPath();
long chunkSize = DefaultChunkSize;
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
        default:
            valid = false;
            break;
    }
}

if (!valid || input is null || output is null)
{
    Console.Error.WriteLine("Usage: Sorter --input <path> --output <path> [--temp <folder>] [--chunk-size <bytes|64MB|1GB>]");
    return 1;
}

if (!File.Exists(input))
{
    Console.Error.WriteLine($"Input file not found: {input}");
    return 1;
}

SortResult result = new ExternalSorter(new SorterOptions(input, output, temp, chunkSize)).Sort();
Console.WriteLine($"Sorted {result.LinesWritten:N0} lines from {result.RunCount:N0} runs to {output}, skipped {result.LinesSkipped:N0} malformed lines.");
return 0;
