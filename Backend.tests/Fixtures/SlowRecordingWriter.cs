using System.Text;

namespace Backend.tests.Fixtures;

/// <summary>
/// text writer that sleeps before recording each write, so output still queued elsewhere is missing from <see cref="Text"/>
/// </summary>
public class SlowRecordingWriter(TimeSpan delay) : TextWriter
{
    readonly StringBuilder recorded = new();
    readonly object gate = new();

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>
    /// everything written so far
    /// </summary>
    public string Text
    {
        get {
            lock (gate)
                return recorded.ToString();
        }
    }

    /// <inheritdoc />
    public override void Write(char value) => Record(value.ToString());

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Record(new string(buffer, index, count));

    /// <inheritdoc />
    public override void Write(string? value) => Record(value ?? string.Empty);

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) => Record(buffer.ToString());

    void Record(string value)
    {
        Thread.Sleep(delay);
        lock (gate) {
            recorded.Append(value);
        }
    }
}
