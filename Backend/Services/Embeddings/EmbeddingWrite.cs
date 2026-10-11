using Backend.Models.Nodes;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Embeddings;

/// <summary>
/// builds the single UPDATE which sets <c>node.embedding</c> from a node's name, content and content type
/// </summary>
public static class EmbeddingWrite {

    /// <summary>
    /// builds the UPDATE which stores the embedding of the composed input, or NULL when the node has no embeddable surface
    /// </summary>
    /// <param name="database">entity manager used to create the operation</param>
    /// <param name="nodeId">id of the row whose embedding is written</param>
    /// <param name="name">node name</param>
    /// <param name="content">content bytes, null when the node has none</param>
    /// <param name="contentType">MIME type of <paramref name="content"/></param>
    /// <returns>operation ready to be executed inside the caller's transaction or standalone</returns>
    public static UpdateValuesOperation<Node> Build(IEntityManager database, long nodeId, string name, byte[] content, string contentType) {
        string composed = EmbeddingInputComposer.Compose(name, content, contentType);
        UpdateValuesOperation<Node> update = database.Update<Node>();

        if (composed == null)
            return update.Set(n => n.Embedding == (float[]) null)
                         .Where(n => n.Id == nodeId);

        return update.Set(n => n.Embedding == EmbeddingExpression.OfText(DB.Constant(composed)).Type<float[]>())
                     .Where(n => n.Id == nodeId);
    }
}
