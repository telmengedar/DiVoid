namespace Backend.Models.Nodes;

/// <summary>
/// a single range-replacement against a node's text content, addressed against the
/// content as it was read (all edits in one request share that original-content frame).
/// the primitive is a splice: replace the half-open range <c>[Start, Start+Length)</c> in
/// <see cref="Unit"/> space with <see cref="Value"/>. every requested edit shape reduces to it:
/// replace (Length&gt;0, Value non-empty), insert (Length=0), delete (Value empty), append
/// (Start at the end, Length=0).
/// </summary>
public class ContentEdit
{
    /// <summary>
    /// addressing space for <see cref="Start"/> and <see cref="Length"/>.
    /// </summary>
    public ContentEditUnit Unit { get; set; }

    /// <summary>
    /// 0-based index of the first unit to replace. must be within <c>[0, count]</c> where
    /// <c>count</c> is the number of lines or characters in the current content.
    /// </summary>
    public int Start { get; set; }

    /// <summary>
    /// number of units to replace, starting at <see cref="Start"/>. 0 makes the edit a pure
    /// insertion at <see cref="Start"/>. <c>Start + Length</c> must not exceed the unit count.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// replacement text spliced in place of the addressed range. null or empty deletes the range.
    /// </summary>
    public string Value { get; set; }
}
