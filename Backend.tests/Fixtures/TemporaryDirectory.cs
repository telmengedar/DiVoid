namespace Backend.tests.Fixtures;

/// <summary>
/// directory with a name of its own which is deleted together with its content on dispose
/// </summary>
public sealed class TemporaryDirectory : IDisposable {
    /// <summary>
    /// creates the directory
    /// </summary>
    /// <param name="create">whether the directory exists on disk; otherwise only its path is reserved</param>
    public TemporaryDirectory(bool create = true) {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "divoid-test-" + Guid.NewGuid().ToString("N"));
        if (create)
            Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// full path of the directory
    /// </summary>
    public string Path { get; }

    /// <inheritdoc />
    public void Dispose() {
        if (Directory.Exists(Path))
            Directory.Delete(Path, true);
    }
}
