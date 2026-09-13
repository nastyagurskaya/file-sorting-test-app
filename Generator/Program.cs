using Generator;
using Shared;

string? output = null;
string? size = null;
int? seed = null;
var valid = args.Length % 2 == 0;

for (var i = 0; valid && i < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--output":
            output = args[i + 1];
            break;
        case "--size":
            size = args[i + 1];
            break;
        case "--seed":
            valid = int.TryParse(args[i + 1], out var parsedSeed);
            if (valid)
            {
                seed = parsedSeed;
            }
            break;
        default:
            valid = false;
            break;
    }
}

if (!valid || output is null || size is null || !SizeParser.TryParse(size, out var sizeBytes))
{
    Console.Error.WriteLine("Usage: Generator --output <path> --size <bytes|10KB|100MB|2GB> [--seed <int>]");
    return 1;
}

var result = new FileGenerator(new GeneratorOptions(output, sizeBytes, seed)).Generate();
Console.WriteLine($"Wrote {result.LinesWritten:N0} lines, {result.BytesWritten:N0} bytes to {output}{(seed is null ? "" : $" (seed {seed})")}.");
return 0;