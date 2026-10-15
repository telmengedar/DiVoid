using Backend.Models.Nodes;
using Backend.Services.Nodes;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Json;

namespace Backend.tests.Fixtures;

/// <summary>
/// runs semantic searches against a node service
/// </summary>
public static class NodeSearch {
    /// <summary>
    /// lists the nodes a filter selects, in the order the service ranks them
    /// </summary>
    /// <param name="service">service to search with</param>
    /// <param name="filter">filter containing the query or queries</param>
    /// <returns>selected nodes, best match first</returns>
    public static async Task<NodeDetails[]> Ranked(NodeService service, NodeFilter filter) {
        AsyncPageResponseWriter<NodeDetails> writer = await service.ListPaged(filter, callerId: 0, isAdmin: true);
        using MemoryStream stream = new();
        await writer.Write(stream);
        Page<NodeDetails> page = Json.Read<Page<NodeDetails>>(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        return page.Result.ToArray();
    }

    /// <summary>
    /// lists the ids of the nodes a filter selects, in the order the service ranks them
    /// </summary>
    /// <param name="service">service to search with</param>
    /// <param name="filter">filter containing the query or queries</param>
    /// <returns>ids of the selected nodes, best match first</returns>
    public static async Task<long[]> RankedIds(NodeService service, NodeFilter filter) {
        return (await Ranked(service, filter)).Select(node => node.Id).ToArray();
    }
}
