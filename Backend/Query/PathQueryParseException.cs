namespace Backend.Query;

/// <summary>
/// thrown by <see cref="PathQueryParser"/> when the path string is syntactically invalid
/// or uses a v1-deferred operator.  Column is 1-based.
/// </summary>
public class PathQueryParseException : Exception
{
    public int Column { get; }

    public PathQueryParseException(int column, string reason)
        : base($"Path query syntax error at column {column}: {reason}")
    {
        Column = column;
    }
}
