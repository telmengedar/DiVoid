using System.Text;
using Backend.Services.Embeddings;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// unit tests for <see cref="EmbeddingInputComposer"/>.
/// pure function — no database or DI needed.
/// covers the full decision matrix from §11 Decision 3 of the embeddings-v2 architecture doc.
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingInputComposerTests
{

    [Test, Parallelizable]
    public void Compose_NameAndTextContent_ReturnsNamePlusSeparatorPlusContent()
    {
        byte[] content = Encoding.UTF8.GetBytes("some body text");
        string result = EmbeddingInputComposer.Compose("My Node", content, "text/plain");

        Assert.That(result, Is.EqualTo("My Node\n\nsome body text"));
    }

    [Test, Parallelizable]
    public void Compose_NameAndNonTextContent_ReturnsNameOnly()
    {
        byte[] content = [0x89, 0x50, 0x4E, 0x47]; // PNG magic bytes
        string result = EmbeddingInputComposer.Compose("ImageNode", content, "image/png");

        Assert.That(result, Is.EqualTo("ImageNode"));
    }

    [Test, Parallelizable]
    public void Compose_NameAndNullContent_ReturnsNameOnly()
    {
        string result = EmbeddingInputComposer.Compose("NameOnly", null, null);

        Assert.That(result, Is.EqualTo("NameOnly"));
    }

    [Test, Parallelizable]
    public void Compose_NameAndEmptyContent_ReturnsNameOnly()
    {
        string result = EmbeddingInputComposer.Compose("NameOnly", [], "text/plain");

        Assert.That(result, Is.EqualTo("NameOnly"));
    }

    [Test, Parallelizable]
    public void Compose_EmptyNameAndTextContent_ReturnsContentOnly()
    {
        byte[] content = Encoding.UTF8.GetBytes("body without a name");
        string result = EmbeddingInputComposer.Compose("", content, "text/markdown");

        Assert.That(result, Is.EqualTo("body without a name"));
    }

    [Test, Parallelizable]
    public void Compose_WhitespaceNameAndTextContent_ReturnsContentOnly()
    {
        byte[] content = Encoding.UTF8.GetBytes("body text");
        string result = EmbeddingInputComposer.Compose("   ", content, "text/plain");

        Assert.That(result, Is.EqualTo("body text"));
    }

    [Test, Parallelizable]
    public void Compose_EmptyNameAndNonTextContent_ReturnsNull()
    {
        byte[] content = [0xFF, 0xD8, 0xFF]; // JPEG magic
        string result = EmbeddingInputComposer.Compose("", content, "image/jpeg");

        Assert.That(result, Is.Null);
    }

    [Test, Parallelizable]
    public void Compose_NullNameAndNullContent_ReturnsNull()
    {
        string result = EmbeddingInputComposer.Compose(null, null, null);

        Assert.That(result, Is.Null);
    }

    [Test, Parallelizable]
    public void Compose_EmptyNameAndNullContent_ReturnsNull()
    {
        string result = EmbeddingInputComposer.Compose("", null, null);

        Assert.That(result, Is.Null);
    }


    [Test, Parallelizable]
    public void Compose_ApplicationJson_TreatedAsText()
    {
        byte[] content = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");
        string result = EmbeddingInputComposer.Compose("JsonNode", content, "application/json");

        Assert.That(result, Is.EqualTo("JsonNode\n\n{\"key\":\"value\"}"));
    }


    [Test, Parallelizable]
    public void Compose_LongContent_ContentTruncatedToFitBudget()
    {
        string name = "Short";
        int separatorLen = 2; // "\n\n"
        int budget = EmbeddingInputComposer.MaxLength - name.Length - separatorLen;
        string longBody = new string('x', budget + 100); // exceeds budget

        byte[] content = Encoding.UTF8.GetBytes(longBody);
        string result = EmbeddingInputComposer.Compose(name, content, "text/plain");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.LessThanOrEqualTo(EmbeddingInputComposer.MaxLength),
            "composed output must not exceed MaxLength");
        Assert.That(result, Does.StartWith("Short\n\n"),
            "name + separator must be preserved at the start");
    }

    [Test, Parallelizable]
    public void Compose_ContentExactlyAtBudget_NotTruncated()
    {
        string name = "N";
        int separatorLen = 2;
        int budget = EmbeddingInputComposer.MaxLength - name.Length - separatorLen;
        string body = new string('y', budget);

        byte[] content = Encoding.UTF8.GetBytes(body);
        string result = EmbeddingInputComposer.Compose(name, content, "text/plain");

        Assert.That(result.Length, Is.EqualTo(EmbeddingInputComposer.MaxLength));
    }

    [Test, Parallelizable]
    public void Compose_LongNameOnly_TruncatedToMaxLength()
    {
        string longName = new string('a', EmbeddingInputComposer.MaxLength + 50);
        string result = EmbeddingInputComposer.Compose(longName, null, null);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.EqualTo(EmbeddingInputComposer.MaxLength));
    }

    [Test, Parallelizable]
    public void Compose_LongTextContentNoName_TruncatedToMaxLength()
    {
        string longBody = new string('z', EmbeddingInputComposer.MaxLength + 200);
        byte[] content = Encoding.UTF8.GetBytes(longBody);
        string result = EmbeddingInputComposer.Compose("", content, "text/plain");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.EqualTo(EmbeddingInputComposer.MaxLength));
    }


    [Test, Parallelizable]
    public void Compose_TextWithCharsetSuffix_TreatedAsText()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello");
        string result = EmbeddingInputComposer.Compose("Node", content, "text/plain; charset=utf-8");

        Assert.That(result, Is.EqualTo("Node\n\nhello"));
    }

    [Test, Parallelizable]
    public void Compose_NonUtf8Bytes_DoesNotThrow()
    {
        byte[] invalidUtf8 = [0x80, 0x81, 0x82]; // continuation bytes without a leading byte
        string result = EmbeddingInputComposer.Compose("MyNode", invalidUtf8, "text/plain");

        Assert.That(result, Is.Not.Null,
            "non-UTF-8 bytes are decoded with replacement — mojibake is acceptable per architecture doc");
    }

    static string Distinct(int length)
    {
        return new string(Enumerable.Range(0, length).Select(i => (char) (0x4E00 + i % 20000)).ToArray());
    }

    static string[] Windows(string? name, string body)
    {
        return EmbeddingInputComposer.ComposeWindows(name!, Encoding.UTF8.GetBytes(body), "text/markdown");
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name longer than the maximum with text content composes to at most the maximum")]
    public void Compose_NameLongerThanMaxLengthWithContent_DoesNotExceedMaxLength()
    {
        string name = new('n', EmbeddingInputComposer.MaxLength + 10);

        string result = EmbeddingInputComposer.Compose(name, Encoding.UTF8.GetBytes("body"), "text/plain");

        Assert.That(result, Is.EqualTo(new string('n', EmbeddingInputComposer.MaxLength)));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: an input whose untruncated composition fits is returned as exactly the single composed string")]
    public void ComposeWindows_InputWithinMaxLength_ReturnsTodaysComposeOutputOnly()
    {
        (string? Name, string Body)[] fixtures = [
            ("My Node", "some body text"),
            (null, "body without a name"),
            ("N", new string('a', EmbeddingInputComposer.MaxLength - 3)),
            (null, new string('b', EmbeddingInputComposer.MaxLength)),
            (null, new string('c', 7900)),
            ("Name", new string('d', 7900 - 6))
        ];

        Assert.Multiple(() => {
            foreach ((string? name, string body) in fixtures)
                Assert.That(Windows(name, body), Is.EqualTo(new[] { EmbeddingInputComposer.Compose(name!, Encoding.UTF8.GetBytes(body), "text/markdown") }));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: the gate is the composed length, so one character over the maximum is windowed")]
    public void ComposeWindows_OneCharacterOverMaxLength_IsWindowed()
    {
        string[] windows = Windows(null, new string('a', EmbeddingInputComposer.MaxLength + 1));

        Assert.That(windows, Has.Length.EqualTo(2));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: windows start at every stride from zero and the last window reaches the end of the content")]
    public void ComposeWindows_LongContent_StartsEveryStrideAndLastWindowReachesEnd()
    {
        string body = Distinct(16094);

        string[] windows = Windows(null, body);

        Assert.That(windows, Is.EqualTo(new[] { body[0..7000], body[3500..10500], body[7000..14000], body[10500..16094] }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: with a name every window carries the name prefix and fits the maximum length")]
    public void ComposeWindows_NamedLongContent_EveryWindowHasNamePrefixAndFitsMaxLength()
    {
        string body = Distinct(16094);

        string[] windows = Windows("Doc", body);

        Assert.Multiple(() => {
            Assert.That(windows, Has.Length.EqualTo(4));
            Assert.That(windows, Has.All.StartWith("Doc\n\n"));
            Assert.That(windows.Select(window => window.Length), Has.All.LessThanOrEqualTo(EmbeddingInputComposer.MaxLength));
            Assert.That(windows[1], Is.EqualTo("Doc\n\n" + body[3500..10500]));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a long name shortens the content slice of every window so the window stays within the maximum length")]
    public void ComposeWindows_LongName_CutsEachWindowToMaxLength()
    {
        string name = new('n', 2000);

        string[] windows = Windows(name, Distinct(20000));

        Assert.Multiple(() => {
            Assert.That(windows, Has.Length.GreaterThan(1));
            Assert.That(windows, Has.All.StartWith(name + "\n\n"));
            Assert.That(windows.Select(window => window.Length), Has.All.EqualTo(EmbeddingInputComposer.MaxLength));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a name which alone fills the maximum leaves no room for content so the node is one window")]
    public void ComposeWindows_NameFillsMaxLength_ReturnsSingleComposeOutput()
    {
        string name = new('n', EmbeddingInputComposer.MaxLength + 5);
        byte[] content = Encoding.UTF8.GetBytes(Distinct(30000));

        string[] windows = EmbeddingInputComposer.ComposeWindows(name, content, "text/markdown");

        Assert.That(windows, Is.EqualTo(new[] { EmbeddingInputComposer.Compose(name, content, "text/markdown") }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: without a name the windows are plain content slices")]
    public void ComposeWindows_UnnamedLongContent_WindowsHaveNoPrefix()
    {
        string body = Distinct(12000);

        string[] windows = Windows("  ", body);

        Assert.That(windows, Is.EqualTo(new[] { body[0..7000], body[3500..10500], body[7000..12000] }));
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: over the cap exactly the maximum number of windows is returned, evenly spaced, with the first at the head and the last at the tail")]
    public void ComposeWindows_OverCap_Returns32WindowsFirstAtHeadLastAtTail()
    {
        const int length = 620000;
        string body = Distinct(length);

        string[] windows = Windows(null, body);

        Assert.Multiple(() => {
            Assert.That(windows, Has.Length.EqualTo(EmbeddingInputComposer.MaxWindows));
            Assert.That(windows, Has.All.Length.EqualTo(EmbeddingInputComposer.WindowLength));
            for (int index = 0; index < windows.Length; index++) {
                int start = (int) Math.Round(index * (double) (length - EmbeddingInputComposer.WindowLength) / (EmbeddingInputComposer.MaxWindows - 1), MidpointRounding.AwayFromZero);
                Assert.That(windows[index], Is.EqualTo(body.Substring(start, EmbeddingInputComposer.WindowLength)), $"window {index}");
            }
            Assert.That(windows[^1], Is.EqualTo(body[^EmbeddingInputComposer.WindowLength..]));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a window boundary inside a surrogate pair is moved left so no pair is split")]
    public void ComposeWindows_BoundaryInsideSurrogatePair_DoesNotSplitPair()
    {
        char[] chars = new string('a', 12000).ToCharArray();
        foreach (int position in new[] { 3499, 6999, 10499 }) {
            chars[position] = '\uD83D';
            chars[position + 1] = '\uDE00';
        }

        string[] windows = Windows(null, new string(chars));

        Assert.Multiple(() => {
            Assert.That(windows, Has.Length.EqualTo(3));
            Assert.That(windows.Select(window => char.IsLowSurrogate(window[0])), Has.All.False);
            Assert.That(windows.Select(window => char.IsHighSurrogate(window[^1])), Has.All.False);
            Assert.That(windows[1][0], Is.EqualTo('\uD83D'));
            Assert.That(windows[0], Has.Length.EqualTo(6999));
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: an input without embeddable surface yields an empty array, never null")]
    public void ComposeWindows_NoSurface_ReturnsEmptyArray()
    {
        Assert.Multiple(() => {
            Assert.That(EmbeddingInputComposer.ComposeWindows(null!, null!, null!), Is.Empty);
            Assert.That(EmbeddingInputComposer.ComposeWindows("  ", [0x89, 0x50], "image/png"), Is.Empty);
        });
    }

    [Test, Parallelizable]
    [Description("DiVoid #16207: a window whose start moved left for a surrogate pair keeps its original end, so the last capped window still reaches the end of the content")]
    public void ComposeWindows_CappedLastStartInsideSurrogatePair_LastWindowKeepsOriginalEndAndReachesTail()
    {
        const int length = 620000;
        char[] chars = Distinct(length).ToCharArray();
        int lastStart = length - EmbeddingInputComposer.WindowLength;
        chars[lastStart - 1] = '\uD83D';
        chars[lastStart] = '\uDE00';
        string body = new(chars);

        string[] windows = Windows(null, body);

        Assert.Multiple(() => {
            Assert.That(windows, Has.Length.EqualTo(EmbeddingInputComposer.MaxWindows));
            Assert.That(windows[^1], Is.EqualTo(body[(lastStart - 1)..]));
            Assert.That(windows[^1], Has.Length.EqualTo(EmbeddingInputComposer.WindowLength + 1));
        });
    }
}
