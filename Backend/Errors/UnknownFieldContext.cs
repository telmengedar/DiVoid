using System.Collections.Generic;

namespace Backend.Errors;

/// <summary>
/// extra context returned alongside the 400 error for an unknown field request.
/// </summary>
class UnknownFieldContext {
    /// <summary>the field name that was not recognised</summary>
    public string Field { get; set; } = "";
    /// <summary>the field names that are available for this endpoint</summary>
    public IReadOnlyList<string> Available { get; set; } = [];
}
