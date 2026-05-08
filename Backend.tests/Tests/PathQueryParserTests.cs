using Backend.Query;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="PathQueryParser"/>.
///
/// Covers: happy paths for every operator in the v1 grammar, edge cases,
/// adversarial inputs, and v1-deferred operators (which must be rejected with
/// a descriptive error pointing at the offending column).
/// </summary>
[TestFixture]
public class PathQueryParserTests
{
    // -----------------------------------------------------------------------
    // Happy paths — single segment
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_SingleSegment_SinglePredicate_ReturnsOneHop()
    {
        PathQuery q = PathQueryParser.Parse("[type:task]");
        Assert.That(q.Hops.Length, Is.EqualTo(1));
        Assert.That(q.Hops[0].Predicates.Length, Is.EqualTo(1));
        Assert.That(q.Hops[0].Predicates[0].Key, Is.EqualTo("type"));
        Assert.That(q.Hops[0].Predicates[0].Values, Is.EqualTo(new[] { "task" }));
    }

    [Test]
    public void Parse_SingleSegment_MultiplePredicates_AndSemantics()
    {
        PathQuery q = PathQueryParser.Parse("[type:task,status:open]");
        Assert.That(q.Hops[0].Predicates.Length, Is.EqualTo(2));
        Assert.That(q.Hops[0].Predicates[0].Key, Is.EqualTo("type"));
        Assert.That(q.Hops[0].Predicates[1].Key, Is.EqualTo("status"));
    }

    [Test]
    public void Parse_WithinKeyOr_Pipe_SplitsIntoMultipleValues()
    {
        PathQuery q = PathQueryParser.Parse("[type:task|question]");
        string[] values = q.Hops[0].Predicates[0].Values;
        Assert.That(values, Is.EqualTo(new[] { "task", "question" }));
    }

    [Test]
    public void Parse_StatusOrValues_TwoValues()
    {
        PathQuery q = PathQueryParser.Parse("[status:open|new]");
        string[] values = q.Hops[0].Predicates[0].Values;
        Assert.That(values, Is.EqualTo(new[] { "open", "new" }));
    }

    [Test]
    public void Parse_IdSegment_ParsesNumericValue()
    {
        PathQuery q = PathQueryParser.Parse("[id:42]");
        Assert.That(q.Hops[0].Predicates[0].Key, Is.EqualTo("id"));
        Assert.That(q.Hops[0].Predicates[0].Values, Is.EqualTo(new[] { "42" }));
    }

    [Test]
    public void Parse_IdSegment_MultipleIds_Via_Pipe()
    {
        PathQuery q = PathQueryParser.Parse("[id:42|43|44]");
        Assert.That(q.Hops[0].Predicates[0].Values, Is.EqualTo(new[] { "42", "43", "44" }));
    }

    [Test]
    public void Parse_EmptySegment_AsFirstHop_Alone_ThrowsParseException()
    {
        // [] as the first (and only) hop is invalid — no constrained seed
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[]"));
        Assert.That(ex.Message, Does.Contain("first hop").IgnoreCase);
    }

    [Test]
    public void Parse_EmptySegment_AsFirstHop_TwoHop_ThrowsParseException()
    {
        // []/[type:task] — first hop unconstrained
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[]/[type:task]"));
        Assert.That(ex.Message, Does.Contain("first hop").IgnoreCase);
    }

    [Test]
    public void Parse_EmptySegment_AsFirstHop_ThreeHop_ThrowsParseException()
    {
        // []/[type:project]/[type:task] — first hop unconstrained
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[]/[type:project]/[type:task]"));
        Assert.That(ex.Message, Does.Contain("first hop").IgnoreCase);
    }

    [Test]
    public void Parse_WildcardNamePercent_PreservedInValue()
    {
        PathQuery q = PathQueryParser.Parse("[name:Di%]");
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("Di%"));
    }

    [Test]
    public void Parse_WildcardNameUnderscore_PreservedInValue()
    {
        PathQuery q = PathQueryParser.Parse("[name:D_Void]");
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("D_Void"));
    }

    // -----------------------------------------------------------------------
    // Happy paths — multi-hop
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_TwoHops_Slash_Separator()
    {
        PathQuery q = PathQueryParser.Parse("[type:organization,name:Pooshit]/[type:project,name:DiVoid]");
        Assert.That(q.Hops.Length, Is.EqualTo(2));
        Assert.That(q.Hops[0].Predicates[0].Key, Is.EqualTo("type"));
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("organization"));
        Assert.That(q.Hops[1].Predicates[1].Values[0], Is.EqualTo("DiVoid"));
    }

    [Test]
    public void Parse_ThreeHops()
    {
        PathQuery q = PathQueryParser.Parse("[type:organization,name:Pooshit]/[type:project,name:DiVoid]/[type:task,status:open]");
        Assert.That(q.Hops.Length, Is.EqualTo(3));
    }

    [Test]
    public void Parse_IdRooted_ThreeHopPath()
    {
        PathQuery q = PathQueryParser.Parse("[id:3]/[type:task]");
        Assert.That(q.Hops.Length, Is.EqualTo(2));
        Assert.That(q.Hops[0].Predicates[0].Key, Is.EqualTo("id"));
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("3"));
        Assert.That(q.Hops[1].Predicates[0].Key, Is.EqualTo("type"));
    }

    [Test]
    public void Parse_TrailingEmptySegment_AnyNeighbour()
    {
        PathQuery q = PathQueryParser.Parse("[type:project,name:DiVoid]/[]");
        Assert.That(q.Hops.Length, Is.EqualTo(2));
        Assert.That(q.Hops[1].Predicates, Is.Empty);
    }

    [Test]
    public void Parse_MixedOrAndMultiKey()
    {
        PathQuery q = PathQueryParser.Parse("[type:organization,name:Pooshit]/[type:project,status:open]/[type:task,status:open|new]");
        Assert.That(q.Hops.Length, Is.EqualTo(3));
        Assert.That(q.Hops[2].Predicates[1].Values, Is.EqualTo(new[] { "open", "new" }));
    }

    // -----------------------------------------------------------------------
    // Quoted strings
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_QuotedValue_WithSpaces()
    {
        PathQuery q = PathQueryParser.Parse("[name:\"My Project\"]");
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("My Project"));
    }

    [Test]
    public void Parse_QuotedValue_WithEscapedQuote()
    {
        PathQuery q = PathQueryParser.Parse("[name:\"he said \\\"hello\\\"\"]");
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("he said \"hello\""));
    }

    [Test]
    public void Parse_QuotedValue_WithEscapedBackslash()
    {
        PathQuery q = PathQueryParser.Parse("[name:\"path\\\\dir\"]");
        Assert.That(q.Hops[0].Predicates[0].Values[0], Is.EqualTo("path\\dir"));
    }

    // -----------------------------------------------------------------------
    // Negated flag is always false in v1 (reserved for later)
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_NegatedFlag_IsAlwaysFalseOnPredicates()
    {
        PathQuery q = PathQueryParser.Parse("[type:task]");
        Assert.That(q.Hops[0].Predicates[0].Negated, Is.False);
    }

    [Test]
    public void Parse_NegatedFlag_IsAlwaysFalseOnSegments()
    {
        PathQuery q = PathQueryParser.Parse("[type:task]");
        Assert.That(q.Hops[0].Negated, Is.False);
    }

    // -----------------------------------------------------------------------
    // Error cases — bad syntax
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_EmptyString_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse(""));
    }

    [Test]
    public void Parse_NullPath_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse(null));
    }

    [Test]
    public void Parse_MissingOpenBracket_ThrowsParseException()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("type:task]"));
        Assert.That(ex.Column, Is.GreaterThan(0));
    }

    [Test]
    public void Parse_MissingCloseBracket_ThrowsParseException()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[type:task"));
        Assert.That(ex.Message, Does.Contain("column").IgnoreCase);
    }

    [Test]
    public void Parse_UnknownKey_ThrowsParseException()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[foo:bar]"));
        Assert.That(ex.Message, Does.Contain("foo"));
        Assert.That(ex.Message, Does.Contain("allowed"));
    }

    [Test]
    public void Parse_MissingColon_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[typetask]"));
    }

    [Test]
    public void Parse_MissingValue_AfterColon_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[type:]"));
    }

    [Test]
    public void Parse_TrailingSlash_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[type:task]/"));
    }

    [Test]
    public void Parse_UnterminatedQuotedString_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[name:\"unclosed]"));
    }

    [Test]
    public void Parse_TrailingCharAfterPath_ThrowsParseException()
    {
        Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[type:task]garbage"));
    }

    // -----------------------------------------------------------------------
    // v1-deferred operators — must be rejected with "reserved" message
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_NegationOnValue_Deferred_Rejected()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[status:!closed]"));
        Assert.That(ex.Message, Does.Contain("reserved").IgnoreCase);
    }

    [Test]
    public void Parse_NegationOnSegment_Deferred_Rejected()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("![type:archived]"));
        Assert.That(ex.Message, Does.Contain("reserved").IgnoreCase);
    }

    [Test]
    public void Parse_ParenthesisedBooleanGroup_Deferred_Rejected()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("([type:task]/[status:open])"));
        Assert.That(ex.Message, Does.Contain("reserved").IgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Error message quality
    // -----------------------------------------------------------------------

    [Test]
    public void Parse_UnknownKey_ErrorMessageContainsColumnNumber()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[badkey:val]"));
        // "column N" should appear in the message
        Assert.That(ex.Message, Does.Contain("column").IgnoreCase);
        Assert.That(ex.Column, Is.GreaterThan(0));
    }

    [Test]
    public void Parse_SyntaxError_MessageContainsColumnNumber()
    {
        var ex = Assert.Throws<PathQueryParseException>(() => PathQueryParser.Parse("[type:task]extra"));
        Assert.That(ex.Message, Does.Contain("column").IgnoreCase);
    }
}
