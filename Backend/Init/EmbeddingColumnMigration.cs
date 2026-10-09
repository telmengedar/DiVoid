using Backend.Services.Embeddings;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Info;

namespace Backend.Init;

/// <summary>
/// converts a legacy <c>real[]</c> <c>node.embedding</c> column to the native vector column,
/// keeping the first <see cref="EmbeddingExpression.Dimensions"/> components of every stored value
/// </summary>
public static class EmbeddingColumnMigration {

    /// <summary>
    /// table which receives the full-size original values before the column is converted
    /// </summary>
    public const string BackupTable = "node_embedding_backup";

    const string LegacyType = "real[]";

    /// <summary>
    /// reads the declared type of <c>node.embedding</c>, null when the table or column does not exist
    /// </summary>
    const string LiveTypeSql = "SELECT format_type(attribute.atttypid, attribute.atttypmod) FROM pg_attribute attribute WHERE attribute.attrelid = to_regclass('node') AND attribute.attname = 'embedding' AND NOT attribute.attisdropped";

    /// <summary>
    /// copies all stored embeddings to <see cref="BackupTable"/>
    /// </summary>
    const string BackupSql = $"CREATE TABLE {BackupTable} AS SELECT id, embedding FROM node WHERE embedding IS NOT NULL";

    /// <summary>
    /// converts the column to <c>vector(<see cref="EmbeddingExpression.Dimensions"/>)</c> keeping the leading components
    /// </summary>
    static readonly string ConvertSql = $"ALTER TABLE node ALTER COLUMN embedding TYPE vector({EmbeddingExpression.Dimensions}) USING subvector(embedding::vector, 1, {EmbeddingExpression.Dimensions})";

    /// <summary>
    /// brings the column to its target type; does nothing on databases without vector support, on a fresh
    /// database and when the column already has the target type
    /// </summary>
    /// <param name="database">access to database</param>
    /// <param name="transaction">schema transaction, the conversion is rolled back with it</param>
    /// <exception cref="InvalidOperationException">the column has a type which is neither legacy nor target</exception>
    public static async Task RunAsync(IEntityManager database, Transaction transaction) {
        IDBInfo dbInfo = database.DBClient.DBInfo;
        if (dbInfo is not PostgreInfo)
            return;

        string liveType = await database.DBClient.ScalarAsync(transaction, LiveTypeSql) as string;
        string targetType = dbInfo.GetVectorType(EmbeddingExpression.Dimensions);
        if (liveType == null || liveType == targetType)
            return;

        if (liveType != LegacyType)
            throw new InvalidOperationException($"Column node.embedding has type '{liveType}', expected '{targetType}' or '{LegacyType}'. To recover, run ALTER TABLE node ALTER COLUMN embedding TYPE {LegacyType} USING NULL, start this version so the migration converts the column, then run backfill-embeddings");

        await database.DBClient.NonQueryAsync(transaction, BackupSql);
        await database.DBClient.NonQueryAsync(transaction, ConvertSql);
    }
}
