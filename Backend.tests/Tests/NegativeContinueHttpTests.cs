using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer tests for <c>continue</c> validation on paged list endpoints.
/// </summary>
[TestFixture, Parallelizable]
public class NegativeContinueHttpTests {
    WebApplicationFactory<Program> factory = null!;
    IHttpService http = null!;

    [OneTimeSetUp]
    public void Setup() {
        factory = TestSetup.CreateTestFactory();
        http = TestSetup.HttpServiceFor(factory);
    }

    [OneTimeTearDown]
    public void TearDown() {
        factory.Dispose();
    }

    [TestCase("/api/nodes?count=2&fields=id&continue=-1", "-1")]
    [TestCase("/api/nodes/links?ids=1&count=2&continue=-1", "-1")]
    [TestCase("/api/nodes?path=%5Btype%3Atask%5D&count=2&fields=id&continue=-1", "-1")]
    [TestCase("/api/nodes?count=2&fields=id&continue=-9223372036854775808", "-9223372036854775808")]
    [Parallelizable]
    public async Task List_NegativeContinue_Returns400BadParameter(string url, string value) {
        HttpResponseMessage response = await http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}{url}");
        string json = await response.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(json);

        Assert.Multiple(() => {
            Assert.That((int)response.StatusCode, Is.EqualTo(400), json);
            Assert.That(error.Code, Is.EqualTo("badparameter"), "error body must be the canonical shape");
            Assert.That(error.Text, Does.Contain($"'{value}'"), "error text must name the offending value");
            Assert.That(error.Text, Does.EndWith("(Parameter 'Continue')"), "error text must name the parameter");
        });
    }

    [TestCase("/api/nodes?count=2&fields=id&continue=0")]
    [TestCase("/api/nodes?count=2&fields=id&continue=1")]
    [TestCase("/api/nodes/links?ids=1&count=2&continue=0")]
    [TestCase("/api/nodes/links?ids=1&count=2&continue=1")]
    [Parallelizable]
    public async Task List_NonNegativeContinue_Returns200(string url) {
        HttpResponseMessage response = await http.Get<HttpResponseMessage>($"{TestSetup.BaseUrl}{url}");

        Assert.That((int)response.StatusCode, Is.EqualTo(200), await response.Content.ReadAsStringAsync());
    }
}
