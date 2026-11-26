namespace Backend.Models.Nodes;

/// <summary>
/// per-node access flags controlling what non-owner non-admin callers may do.
/// owner and admin always override these flags.
/// </summary>
[Flags]
public enum NodeAccess
{
    /// <summary>private — only owner and admin may read or write</summary>
    None = 0,

    /// <summary>everyone authenticated for read may GET, GET content, see in lists</summary>
    Read = 1,

    /// <summary>everyone authenticated for write may PATCH, POST content, embed-op, DELETE</summary>
    Write = 2,

    /// <summary>Members of the owning organization may read.</summary>
    OrgRead = 4,

    /// <summary>Members of the owning organization may write.</summary>
    OrgWrite = 8
}
