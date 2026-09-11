namespace Tests;

sealed class TempWorkspace : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("file-sorting-tests-").FullName;

    public string Input => Path.Combine(root, "input.txt");

    public string Output => Path.Combine(root, "output.txt");

    public string Temp => Path.Combine(root, "runs");

    public void Dispose() => Directory.Delete(root, recursive: true);
}
