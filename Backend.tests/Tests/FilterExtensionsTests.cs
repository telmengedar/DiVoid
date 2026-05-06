using Backend.Extensions;
using Backend.Models.Nodes;
using Backend.tests.Fixtures;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.tests.Tests;

[TestFixture]
public class FilterExtensionsTests
{
    // -----------------------------------------------------------------------
    // ContainsWildcards
    // -----------------------------------------------------------------------

    [Test]
    public void ContainsWildcards_Percent_ReturnsTrue()
        => Assert.That("hello%world".ContainsWildcards(), Is.True);

    [Test]
    public void ContainsWildcards_Underscore_ReturnsTrue()
        => Assert.That("hello_world".ContainsWildcards(), Is.True);

    [Test]
    public void ContainsWildcards_NoWildcard_ReturnsFalse()
        => Assert.That("helloworld".ContainsWildcards(), Is.False);

    [Test]
    public void ContainsWildcards_EmptyString_ReturnsFalse()
        => Assert.That("".ContainsWildcards(), Is.False);

    // -----------------------------------------------------------------------
    // ApplyFilter — PageFilter (count / offset clamping)
    // -----------------------------------------------------------------------

    [Test]
    public void ApplyFilter_NullCount_ClampsTo500()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = null };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        op.ApplyFilter(filter);
        Assert.That(filter.Count, Is.EqualTo(500));
    }

    [Test]
    public void ApplyFilter_CountOver500_ClampsTo500()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = 1000 };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        op.ApplyFilter(filter);
        Assert.That(filter.Count, Is.EqualTo(500));
    }

    [Test]
    public void ApplyFilter_CountExactly500_NotClamped()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = 500 };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        op.ApplyFilter(filter);
        Assert.That(filter.Count, Is.EqualTo(500));
    }

    [Test]
    public void ApplyFilter_CountBelow500_NotClamped()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = 10 };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        op.ApplyFilter(filter);
        Assert.That(filter.Count, Is.EqualTo(10));
    }

    [Test]
    public void ApplyFilter_ZeroCount_ThrowsArgumentException()
    {
        using DatabaseFixture fixture = new();
        // Count = 0 should throw even after the null-clamp branch is skipped
        // because the guard runs on the (possibly already-set) value.
        // We set Count explicitly to 0 and ignoreLimits = false so no clamping.
        PageFilter filter = new() { Count = 0 };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        Assert.Throws<ArgumentException>(() => op.ApplyFilter(filter));
    }

    [Test]
    public void ApplyFilter_NegativeCount_ThrowsArgumentException()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = -5 };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        Assert.Throws<ArgumentException>(() => op.ApplyFilter(filter));
    }

    [Test]
    public void ApplyFilter_IgnoreLimits_DoesNotClamp()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = null };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        // With ignoreLimits=true the clamping branch is skipped entirely.
        // Count stays null and no exception is thrown even though Count is null.
        Assert.DoesNotThrow(() => op.ApplyFilter(filter, ignoreLimits: true));
        Assert.That(filter.Count, Is.Null);
    }

    [Test]
    public void ApplyFilter_WithContinue_AppliesOffset()
    {
        using DatabaseFixture fixture = new();
        PageFilter filter = new() { Count = 10, Continue = 50 };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        // Should not throw; we can't easily inspect the built SQL but exercising the
        // path is sufficient for a baseline.
        Assert.DoesNotThrow(() => op.ApplyFilter(filter));
    }

    // -----------------------------------------------------------------------
    // ApplyFilter — ListFilter (sort)
    // -----------------------------------------------------------------------

    [Test]
    public void ApplyFilter_SortOnePartField_DoesNotThrow()
    {
        using DatabaseFixture fixture = new();
        ListFilter filter = new() { Count = 10, Sort = "name" };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        Assert.DoesNotThrow(() => op.ApplyFilter(filter));
    }

    [Test]
    public void ApplyFilter_SortTwoPartField_DoesNotThrow()
    {
        using DatabaseFixture fixture = new();
        ListFilter filter = new() { Count = 10, Sort = "node.name" };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        Assert.DoesNotThrow(() => op.ApplyFilter(filter));
    }

    [Test]
    public void ApplyFilter_SortDescending_DoesNotThrow()
    {
        using DatabaseFixture fixture = new();
        ListFilter filter = new() { Count = 10, Sort = "name", Descending = true };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        Assert.DoesNotThrow(() => op.ApplyFilter(filter));
    }

    [Test]
    public void ApplyFilter_NoSort_DoesNotThrow()
    {
        using DatabaseFixture fixture = new();
        ListFilter filter = new() { Count = 10, Sort = null };
        LoadOperation<Node> op = fixture.EntityManager.Load<Node>();
        Assert.DoesNotThrow(() => op.ApplyFilter(filter));
    }

    // -----------------------------------------------------------------------
    // ApplyFilter — ListFilter with mapper
    // -----------------------------------------------------------------------

    [Test]
    public void ApplyFilter_WithMapper_SortByMappedField_DoesNotThrow()
    {
        using DatabaseFixture fixture = new();
        NodeMapper mapper = new();
        // The NodeMapper registers fields as "id", "type", "name" — use the exact registered key.
        NodeFilter filter = new() { Count = 10, Sort = "name" };
        LoadOperation<Node> op = mapper.CreateOperation(fixture.EntityManager, mapper.DefaultListFields.Select(f => mapper[f].Field).ToArray());
        Assert.DoesNotThrow(() => op.ApplyFilter(filter, mapper));
    }
}
