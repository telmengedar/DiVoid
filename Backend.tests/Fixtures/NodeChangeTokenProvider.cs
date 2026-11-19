using Pooshit.Http;

namespace Backend.tests.Fixtures;

internal sealed class NodeChangeTokenProvider(string token) : ITokenProvider {
    /// <inheritdoc />
    public string Method => "Bearer";

    /// <inheritdoc />
    public Task<string> GetTokenAsync() => Task.FromResult(token);
}
