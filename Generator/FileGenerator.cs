using System.Text;
using Shared;

namespace Generator;

record GeneratorOptions(string Output, long SizeBytes, int? Seed);

record GenerationResult(long LinesWritten, long BytesWritten);

sealed class FileGenerator(GeneratorOptions options)
{
    const int BufferSize = 1024 * 1024;

    static readonly string[] TextPool = BuildTextPool();

    // No seed means a different file on every run; a seed makes the output reproducible.
    readonly Random random = options.Seed is int seed ? new Random(seed) : new Random();

    public GenerationResult Generate()
    {
        using StreamWriter writer = new(options.Output, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), BufferSize);

        writer.NewLine = "\n";

        long lines = 0;
        long bytes = 0;

        // Stops at the first line that reaches the target, so the file may overshoot by one line.
        while (bytes < options.SizeBytes)
        {
            var line = new LineRecord(NextNumber(), TextPool[random.Next(TextPool.Length)]).ToString();
            var byteLength = Encoding.UTF8.GetByteCount(line);
            writer.WriteLine(line);
            bytes += byteLength + 1;
            lines++;
        }

        return new GenerationResult(lines, bytes);
    }

    int NextNumber() => random.Next(1, 100000);

    static string[] BuildTextPool()
    {
        string[] special =
        [
            "A", "Z", "a", "z",
            "Apple", "apple", "APPLE", "Apple pie", "Apple pie filling", "Application", "Applications",
            "Banana", "banana", "Banana split",
            "Cherry", "cherry", "Cherry 2.0", "Cherry is the best", "32. Cherry is the best",
            "Date", "date", "Date 1.1.2026",
            "Fig", "Fig 3",
            "Grape", "Grape 1.5 kg",
            "Version 1.9", "Version 1.10",
            "Item 42", "Item 042",
            "Zebra", "zebra",
            "Olive oil 0.5 l", "Olive oil 0.75 l",
            "Mango", "Mango 100%",
            "Kiwi", "kiwi 2",
            "Quince jam, 3 jars",
            "A very long text value that exists to mix line lengths in the generated file"
        ];

        string[] adjectives =
        [
            "Red", "Green", "Blue", "Yellow", "Purple", "Orange", "Silver", "Golden", "Bitter",
            "Sweet", "Sour", "Fresh", "Frozen", "Dried", "Wild", "Sunny", "Cloudy", "Quiet",
            "Loud", "Ancient", "Modern", "Hidden", "Bright", "Gentle"
        ];

        string[] nouns =
        [
            "Apple", "Banana", "Cherry", "Date", "Elderberry", "Fig", "Grape", "Honeydew", "Kiwi",
            "Lemon", "Mango", "Nectarine", "Olive", "Peach", "Quince", "Raspberry", "Strawberry",
            "Tangerine", "Walnut", "Apricot", "Blueberry", "Cranberry", "Dragonfruit", "Grapefruit",
            "Lime", "Melon", "Papaya", "Pear", "Plum", "Pomegranate", "Pumpkin", "Currant", "Coconut",
            "Almond", "Chestnut", "Hazelnut", "Pistachio", "Blackberry", "Gooseberry", "Persimmon"
        ];

        var pool = new string[special.Length + (adjectives.Length * nouns.Length)];
        special.CopyTo(pool, 0);
        var index = special.Length;

        foreach (var adjective in adjectives)
        {
            foreach (var noun in nouns)
            {
                pool[index++] = $"{adjective} {noun}";
            }
        }

        return pool;
    }
}
