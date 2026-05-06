using Backend.Services.Auth;

namespace Backend.tests.Tests;

[TestFixture]
public class KeyGeneratorTests
{
    [Test]
    public void GenerateKey_LengthOne_ProducesSixCharacterString()
    {
        // Each integer → 6 chars (see TokenFromNumbers: 6 yields per number)
        KeyGenerator gen = new();
        string key = gen.GenerateKey(1);
        Assert.That(key.Length, Is.EqualTo(6));
    }

    [Test]
    public void GenerateKey_LengthSixteen_Produces96CharacterString()
    {
        // 16 * 6 = 96
        KeyGenerator gen = new();
        string key = gen.GenerateKey(16);
        Assert.That(key.Length, Is.EqualTo(96));
    }

    [Test]
    public void GenerateKey_ContainsOnlyAllowedChars()
    {
        KeyGenerator gen = new();
        string key = gen.GenerateKey(16);
        char[] allowed = "W96RXSE817FT3UA0MP4KB2LHY5NDCIGZ".ToCharArray();
        // All characters in the generated key must be in the alphabet.
        foreach (char c in key)
            Assert.That(allowed, Does.Contain(c), $"Unexpected character '{c}' in key");
    }

    [Test]
    public void GenerateKey_TwoConsecutiveCalls_ProduceDifferentKeys()
    {
        // Probabilistic: the chance of two identical 96-char keys is negligible.
        KeyGenerator gen = new();
        string key1 = gen.GenerateKey(16);
        string key2 = gen.GenerateKey(16);
        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void GenerateKey_LengthZero_ProducesEmptyString()
    {
        KeyGenerator gen = new();
        string key = gen.GenerateKey(0);
        Assert.That(key, Is.EqualTo(""));
    }
}
