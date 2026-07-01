using Backend;
using Backend.Services.Embeddings;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// validates <see cref="Startup.BuildEmbeddingProvider"/> config → provider selection
/// and the dimension fail-closed gate.
///
/// load-bearing (DiVoid #275): these tests pin that:
///   - absent / None config → NullEmbeddingProvider
///   - GoogleMl config → GoogleMlEmbeddingProvider with IsEnabled = true
///   - Http config → HttpEmbeddingProvider with IsEnabled = true
///   - wrong dimension → InvalidOperationException (fail-closed)
///   - unknown provider name → InvalidOperationException (fail-closed)
/// </summary>
[TestFixture]
public class EmbeddingProviderSelectionTests
{
    static IConfiguration BuildConfig(params (string key, string value)[] pairs) {
        Dictionary<string, string?> dict = new();
        foreach ((string key, string value) in pairs)
            dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // -----------------------------------------------------------------------
    // PS1 — absent Embedding:Provider → NullEmbeddingProvider
    // -----------------------------------------------------------------------

    [Test]
    public void PS1_NoProviderConfig_ReturnsNullProvider()
    {
        IConfiguration config = BuildConfig();
        IEmbeddingProvider provider = Startup.BuildEmbeddingProvider(config);
        Assert.That(provider, Is.InstanceOf<NullEmbeddingProvider>(),
            "PS1: absent Embedding:Provider must return NullEmbeddingProvider");
        Assert.That(provider.IsEnabled, Is.False);
    }

    // -----------------------------------------------------------------------
    // PS2 — Embedding:Provider = None → NullEmbeddingProvider
    // -----------------------------------------------------------------------

    [Test]
    public void PS2_NoneProvider_ReturnsNullProvider()
    {
        IConfiguration config = BuildConfig(("Embedding:Provider", "None"));
        IEmbeddingProvider provider = Startup.BuildEmbeddingProvider(config);
        Assert.That(provider, Is.InstanceOf<NullEmbeddingProvider>(),
            "PS2: Embedding:Provider=None must return NullEmbeddingProvider");
    }

    // -----------------------------------------------------------------------
    // PS3 — Embedding:Provider = GoogleMl → GoogleMlEmbeddingProvider
    // -----------------------------------------------------------------------

    [Test]
    public void PS3_GoogleMlProvider_ReturnsGoogleMlProvider()
    {
        IConfiguration config = BuildConfig(("Embedding:Provider", "GoogleMl"));
        IEmbeddingProvider provider = Startup.BuildEmbeddingProvider(config);
        Assert.That(provider, Is.InstanceOf<GoogleMlEmbeddingProvider>(),
            "PS3: Embedding:Provider=GoogleMl must return GoogleMlEmbeddingProvider");
        Assert.That(provider.IsEnabled, Is.True, "PS3: GoogleMlEmbeddingProvider must have IsEnabled=true");
        Assert.That(provider.Dimension, Is.EqualTo(EmbeddingCompositionPolicy.EmbeddingDimension),
            "PS3: default dimension must equal EmbeddingCompositionPolicy.EmbeddingDimension");
    }

    // -----------------------------------------------------------------------
    // PS4 — Embedding:Provider = Http → HttpEmbeddingProvider
    // -----------------------------------------------------------------------

    [Test]
    public void PS4_HttpProvider_ReturnsHttpProvider()
    {
        IConfiguration config = BuildConfig(
            ("Embedding:Provider", "Http"),
            ("Embedding:Endpoint", "http://localhost:11434/v1/embeddings"),
            ("Embedding:Model", "nomic-embed-text"),
            ("Embedding:Dimension", EmbeddingCompositionPolicy.EmbeddingDimension.ToString())
        );
        IEmbeddingProvider provider = Startup.BuildEmbeddingProvider(config);
        Assert.That(provider, Is.InstanceOf<HttpEmbeddingProvider>(),
            "PS4: Embedding:Provider=Http must return HttpEmbeddingProvider");
        Assert.That(provider.IsEnabled, Is.True, "PS4: HttpEmbeddingProvider must have IsEnabled=true");
    }

    // -----------------------------------------------------------------------
    // PS5 — Http provider missing Endpoint → InvalidOperationException
    // -----------------------------------------------------------------------

    [Test]
    public void PS5_HttpProvider_MissingEndpoint_ThrowsInvalidOperation()
    {
        IConfiguration config = BuildConfig(
            ("Embedding:Provider", "Http"),
            ("Embedding:Model", "some-model"),
            ("Embedding:Dimension", EmbeddingCompositionPolicy.EmbeddingDimension.ToString())
        );
        Assert.Throws<InvalidOperationException>(() => Startup.BuildEmbeddingProvider(config),
            "PS5: Http provider without Endpoint must throw InvalidOperationException");
    }

    // -----------------------------------------------------------------------
    // PS6 — dimension mismatch → fail-closed (InvalidOperationException)
    // -----------------------------------------------------------------------

    [Test]
    public void PS6_HttpProvider_WrongDimension_ThrowsFailClosed()
    {
        IConfiguration config = BuildConfig(
            ("Embedding:Provider", "Http"),
            ("Embedding:Endpoint", "http://localhost:11434/v1/embeddings"),
            ("Embedding:Model", "nomic-embed-text"),
            ("Embedding:Dimension", "768")  // wrong — must be 3072
        );
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => Startup.BuildEmbeddingProvider(config),
            "PS6: provider dimension 768 != EmbeddingDimension 3072 must throw");
        Assert.That(ex.Message, Does.Contain("768").And.Contain("3072"),
            "PS6: error message must mention both the declared dimension and the required dimension");
    }

    // -----------------------------------------------------------------------
    // PS7 — unknown provider name → fail-closed
    // -----------------------------------------------------------------------

    [Test]
    public void PS7_UnknownProvider_ThrowsInvalidOperation()
    {
        IConfiguration config = BuildConfig(("Embedding:Provider", "AwsBedrockXYZ"));
        Assert.Throws<InvalidOperationException>(() => Startup.BuildEmbeddingProvider(config),
            "PS7: unknown Embedding:Provider value must throw InvalidOperationException");
    }
}
