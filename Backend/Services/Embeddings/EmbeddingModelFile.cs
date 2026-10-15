namespace Backend.Services.Embeddings;

/// <summary>
/// file of the local embedding model, downloaded from <paramref name="Url"/> and verified against <paramref name="Sha256"/>
/// </summary>
/// <param name="Name">file name inside the model directory</param>
/// <param name="Url">address the file is downloaded from</param>
/// <param name="Sha256">expected SHA-256 of the file as lower case hex</param>
public record EmbeddingModelFile(string Name, string Url, string Sha256);
