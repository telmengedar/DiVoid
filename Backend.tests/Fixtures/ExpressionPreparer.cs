using Backend.Models.Nodes;
using Moq;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Info;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Fixtures;

/// <summary>
/// renders a vector expression into the command text and parameters of a Postgres statement which stores it
/// </summary>
public static class ExpressionPreparer {

    /// <summary>
    /// prepares an update which sets the embedding of a node to an expression
    /// </summary>
    /// <param name="expression">vector expression to render</param>
    /// <returns>prepared update carrying the command text and the bound parameters</returns>
    public static PreparedOperation Prepare(ISqlToken expression) {
        Mock<IDBClient> client = new();
        client.SetupGet(c => c.DBInfo).Returns(new PostgreInfo());
        return new EntityManager(client.Object).Update<Node>()
                                               .Set(n => n.Embedding == expression.Type<float[]>())
                                               .Where(n => n.Id == 1)
                                               .Prepare();
    }
}
