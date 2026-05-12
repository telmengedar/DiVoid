namespace Backend.Models.Attributes;

/// <summary>
/// Marks an entity string property whose value is a JSON-encoded representation of a
/// richer type (e.g. <c>string[]</c>). When <see cref="Backend.Extensions.DatabasePatchExtensions"/>
/// encounters a patch operation targeting a property with this attribute and the inbound
/// value is not already a JSON string, it JSON-encodes the value before writing it to
/// the DB column - so callers can supply the natural shape (e.g. an array) rather than a
/// pre-encoded JSON string.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class JsonColumnAttribute : Attribute { }
