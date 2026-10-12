using System;
using Backend.Models.Nodes;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// builds the single UPDATE which sets <c>node.embedding</c> from a node's name, content and content type
/// </summary>
public static class EmbeddingWrite {

    /// <summary>
    /// builds the UPDATE which stores the embedding of the composed windows, or NULL when the node has no embeddable surface
    /// </summary>
    /// <param name="database">entity manager used to create the operation</param>
    /// <param name="nodeId">id of the row whose embedding is written</param>
    /// <param name="name">node name</param>
    /// <param name="content">content bytes, null when the node has none</param>
    /// <param name="contentType">MIME type of <paramref name="content"/></param>
    /// <param name="lastUpdate">when set, the UPDATE only applies while the row still has this <c>lastUpdate</c></param>
    /// <returns>operation ready to be executed inside the caller's transaction or standalone</returns>
    public static UpdateValuesOperation<Node> Build(IEntityManager database, long nodeId, string name, byte[] content, string contentType, DateTime? lastUpdate = null) {
        string[] windows = EmbeddingInputComposer.ComposeWindows(name, content, contentType);
        UpdateValuesOperation<Node> update = database.Update<Node>();

        if (windows.Length == 0)
            return update.Set(n => n.Embedding == (float[]) null)
                         .Where(Target(nodeId, lastUpdate).Content);

        return update.Set(n => n.Embedding == EmbeddingExpression.OfTexts(windows).Type<float[]>())
                     .Where(Target(nodeId, lastUpdate).Content);
    }

    static PredicateExpression<Node> Target(long nodeId, DateTime? lastUpdate) {
        PredicateExpression<Node> target = new PredicateExpression<Node>(n => n.Id == nodeId);
        if (lastUpdate == null)
            return target;

        DateTime expected = lastUpdate.Value;
        return target & new PredicateExpression<Node>(n => n.LastUpdate == expected);
    }
}
