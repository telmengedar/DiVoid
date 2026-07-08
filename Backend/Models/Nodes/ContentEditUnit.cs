namespace Backend.Models.Nodes;

/// <summary>
/// addressing space a <see cref="ContentEdit"/> uses to locate the region it replaces.
/// </summary>
public enum ContentEditUnit
{
    /// <summary>
    /// address by 0-based line index. lines are delimited by <c>\n</c>; a line's range
    /// includes its trailing <c>\n</c> terminator. a bare <c>\r</c> is treated as ordinary
    /// content, so <c>\r\n</c> files are edited without disturbing the carriage return.
    /// </summary>
    Line,

    /// <summary>
    /// address by 0-based Unicode code-point (character) index. offsets are code points,
    /// never bytes and never UTF-16 units, so a multi-byte character or surrogate pair can
    /// never be split by a range boundary.
    /// </summary>
    Char
}
