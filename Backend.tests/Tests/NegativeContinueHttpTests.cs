using System.Net.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Pooshit.AspNetCore.Services.Errors;
using Pooshit.Http;
using Pooshit.Http.Paths;
using Pooshit.Json;

namespace Backend.tests.Tests;

/// <summary>
/// HTTP-layer tests for <c>continue</c> validation on paged list endpoints.
/// </summary>
[TestFixture, Parallelizable]
public class NegativeContinueHttpTests {
    WebApplicationFactory<Program> factory = null!;

    [OneTimeSetUp]
    public void Setup() {
        factory = TestSetup.CreateTestFactory();
    }

    [OneTimeTearDown]
    public void TearDown() {
        factory.Dispose();
    }

    static string ListUrl(string route, string? filterName, string? filterValue, long continueValue) {
        QueryParameters query = new("count", 2);
        if (filterName != null)
            query.Add(filterName, filterValue);
        query.Add("continue", continueValue);
        return Rest.PathQuery(query, ["http://localhost", "api", ..route.Split('/')]);
    }

    [TestCase("nodes", null, null, -1L)]
    [TestCase("nodes/links", "ids", "1", -1L)]
    [TestCase("nodes", "path", "[type:task]", -1L)]
    [TestCase("nodes", null, null, long.MinValue)]
    [Parallelizable]
    public async Task List_NegativeContinue_Returns400BadParameter(string route, string? filterName, string? filterValue, long continueValue) {
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage response = await http.Get<HttpResponseMessage>(ListUrl(route, filterName, filterValue, continueValue));
        string json = await response.Content.ReadAsStringAsync();
        ErrorResponse error = Json.Read<ErrorResponse>(json);

        Assert.Multiple(() => {
            Assert.That((int)response.StatusCode, Is.EqualTo(400), json);
            Assert.That(error.Code, Is.EqualTo("badparameter"), "error body must be the canonical shape");
            Assert.That(error.Text, Does.Contain($"'{continueValue}'"), "error text must name the offending value");
            Assert.That(error.Text, Does.EndWith("(Parameter 'Continue')"), "error text must name the parameter");
        });
    }

    [TestCase("nodes", null, null, 0L)]
    [TestCase("nodes", null, null, 1L)]
    [TestCase("nodes/links", "ids", "1", 0L)]
    [TestCase("nodes/links", "ids", "1", 1L)]
    [Parallelizable]
    public async Task List_NonNegativeContinue_Returns200(string route, string? filterName, string? filterValue, long continueValue) {
        IHttpService http = TestSetup.HttpServiceFor(factory);

        HttpResponseMessage response = await http.Get<HttpResponseMessage>(ListUrl(route, filterName, filterValue, continueValue));

        Assert.That((int)response.StatusCode, Is.EqualTo(200), await response.Content.ReadAsStringAsync());
    }
}
