using Generator;

string? output = null;
string? size = null;
int? seed = null;
bool valid = args.Length % 2 == 0;

for (int i = 0; valid && i < args.Length; i += 2)
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
            valid = int.TryParse(args[i + 1], out int parsedSeed);
            if (valid) seed = parsedSeed;
            break;
        default:
            valid = false;
            break;
    }
}

if (!valid || output is null || size is null || !TryParseSize(size, out long sizeBytes))
{
    Console.Error.WriteLine("Usage: Generator --output <path> --size <bytes|10KB|100MB|2GB> [--seed <int>]");
    return 1;
}

(long lines, long bytes) = new FileGenerator(new GeneratorOptions(output, sizeBytes, seed)).Generate();
Console.WriteLine($"Wrote {lines:N0} lines, {bytes:N0} bytes to {output}{(seed is null ? "" : $" (seed {seed})")}.");
return 0;

static bool TryParseSize(string value, out long bytes)
{
    bytes = 0;
    value = value.Trim().ToUpperInvariant();

    long multiplier = value switch
    {
        _ when value.EndsWith("GB") => 1024L * 1024 * 1024,
        _ when value.EndsWith("MB") => 1024L * 1024,
        _ when value.EndsWith("KB") => 1024L,
        _ => 1L
    };

    string number = multiplier == 1 ? value : value[..^2];
    if (!long.TryParse(number, out long n) || n <= 0) return false;

    bytes = n * multiplier;
    return true;
}