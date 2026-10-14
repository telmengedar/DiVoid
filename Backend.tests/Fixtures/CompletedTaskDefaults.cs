using Moq;

namespace Backend.tests.Fixtures;

/// <summary>
/// mock default values which complete the affected-row tasks of a mocked database client
/// </summary>
public class CompletedTaskDefaults : DefaultValueProvider
{
    /// <inheritdoc />
    protected override object GetDefaultValue(Type type, Mock mock)
    {
        if (type == typeof(Task<int>))
            return Task.FromResult(1);
        if (type == typeof(Task<object>))
            return Task.FromResult<object>(1L);
        return (type.IsValueType ? Activator.CreateInstance(type) : null)!;
    }
}
