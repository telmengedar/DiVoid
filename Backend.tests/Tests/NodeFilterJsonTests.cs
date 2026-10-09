using Backend.Models.Nodes;
using NUnit.Framework;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// Pooshit.Json deserialisation of <see cref="NodeFilter"/> (closed type set, DiVoid #11106).
/// </summary>
[TestFixture, Parallelizable]
public class NodeFilterJsonTests
{
    [Test, Parallelizable]
    [Description("Pooshit.Json supports T[] but not List: Queries must stay an array for any JSON path")]
    public void Queries_DeserialisesFromJsonArray()
    {
        NodeFilter filter = Json.Read<NodeFilter>("{\"queries\":[\"alpha, beta\",\"gamma\"]}");

        Assert.That(filter.Queries, Is.EqualTo(new[] { "alpha, beta", "gamma" }));
    }
}
