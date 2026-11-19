namespace Backend.Models.Nodes;

/// <summary>The direct root and optional exact selectors of one change stream.</summary>
public class NodeChangeFilter {
    /// <summary>The required readable direct-root anchor.</summary>
    public long RootNodeId { get; set; }

    /// <summary>The optional explicit root or direct-member identifiers.</summary>
    public long[] Id { get; set; }

    /// <summary>The optional exact direct-member types.</summary>
    public string[] Type { get; set; }

    /// <summary>The selected change categories, with zero selecting all.</summary>
    public NodeHookEvents Events { get; set; }

    internal NodeChangeFilter Validate() {
        if (RootNodeId <= 0 || Id?.Any(id => id <= 0) == true || Type?.Any(type => string.IsNullOrWhiteSpace(type) || type.Length > 128) == true)
            throw new ArgumentException("A positive root, positive IDs and nonblank types of at most 128 characters are required.");
        long[] ids = Id?.Distinct().ToArray() ?? [];
        string[] types = Type?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (ids.Length > 64 || types.Length > 16 || ((int)Events & ~(int)NodeHookEvents.All) != 0)
            throw new ArgumentException("At most 64 IDs, 16 exact types and valid event flags are supported.");
        return new NodeChangeFilter { RootNodeId = RootNodeId, Id = ids, Type = types, Events = Events == 0 ? NodeHookEvents.All : Events };
    }
}
