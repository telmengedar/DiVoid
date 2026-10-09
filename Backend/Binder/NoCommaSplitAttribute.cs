namespace mamgo.services.Binding;

/// <summary>
/// marks an array property that <see cref="ArrayParameterBinderProvider"/> must not split on commas
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NoCommaSplitAttribute : Attribute;
