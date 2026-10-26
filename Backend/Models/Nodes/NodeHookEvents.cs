namespace Backend.Models.Nodes;

/// <summary>change kinds a node hook can subscribe to</summary>
[Flags]
public enum NodeHookEvents {
    /// <summary>status changes</summary>
    Status = 1,

    /// <summary>content writes</summary>
    Content = 2,

    /// <summary>links added, removed or changed</summary>
    Links = 4,

    /// <summary>every other node field change</summary>
    Metadata = 8,

    /// <summary>every change kind</summary>
    All = Status | Content | Links | Metadata
}
