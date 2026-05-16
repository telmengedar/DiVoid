using Pooshit.AspNetCore.Services.Data;

namespace Backend.Models.Messages;

/// <summary>
/// filter for <see cref="Message"/> list operations.
/// inherits paging, sort, fields, and continuation from <see cref="ListFilter"/>.
/// the <c>Query</c> field on <see cref="ListFilter"/> is unused — messages have no embedding
/// and no semantic-search angle; do not add wildcard subject filtering here without also
/// adding the ContainsWildcards / LIKE fan-out logic from NodeService.GenerateFilter.
/// </summary>
public class MessageFilter : ListFilter {

    /// <summary>
    /// filter to messages with one of the specified recipient ids.
    /// combined with the array binder, supports <c>?recipientId=1,2,3</c>, <c>?recipientId=[1,2,3]</c>,
    /// and repeated <c>?recipientId=1&amp;recipientId=2</c>.
    /// for non-admin callers this filter is ANDed on top of the principal-scoping clause,
    /// not in place of it — callers can only narrow within their visible set.
    /// </summary>
    public long[] RecipientId { get; set; }

    /// <summary>
    /// filter to messages with one of the specified author ids.
    /// same array-binder shapes as <see cref="RecipientId"/>.
    /// for non-admin callers this filter is ANDed on top of the principal-scoping clause.
    /// </summary>
    public long[] AuthorId { get; set; }
}
