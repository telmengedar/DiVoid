using Backend.Services.Embeddings;
using NUnit.Framework;

namespace Backend.tests.Tests;

/// <summary>
/// pins the normalisation of model output and the guard against output which can not be stored
/// </summary>
[TestFixture, Parallelizable]
public class EmbeddingVectorTests {

    [Test, Parallelizable]
    [Description("A vector is scaled to length one without changing its direction.")]
    public void Normalize_ThreeFourFive_ScalesToLengthOneKeepingDirection() {
        float[] normalized = EmbeddingVector.Normalize([3f, 4f, 0f]);

        Assert.That(normalized, Is.EqualTo(new[] { 0.6f, 0.8f, 0f }).Within(1e-6));
    }


    [Test, Parallelizable]
    [Description("The input vector is left as it was.")]
    public void Normalize_Vector_DoesNotChangeInput() {
        float[] input = [3f, 4f];

        EmbeddingVector.Normalize(input);

        Assert.That(input, Is.EqualTo(new[] { 3f, 4f }));
    }


    [Test, Parallelizable]
    [Description("A vector of length one is not scaled again.")]
    public void Normalize_UnitVector_StaysUnchanged() {
        float[] unit = EmbeddingVector.Normalize([0.6f, 0.8f]);

        Assert.That(EmbeddingVector.Normalize(unit), Is.EqualTo(unit).Within(1e-6));
    }


    [Test, Parallelizable]
    [Description("The length is summed in double precision, so 768 large components do not overflow float.")]
    public void Normalize_LargeComponents_ProducesFiniteUnitVector() {
        float[] large = Enumerable.Repeat(1e20f, EmbeddingExpression.Dimensions).ToArray();

        float[] normalized = EmbeddingVector.Normalize(large);

        Assert.That(normalized.Sum(component => (double) component * component), Is.EqualTo(1.0).Within(1e-5));
    }


    [Test, Parallelizable]
    [Description("A component which is not finite is refused instead of being stored.")]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void Normalize_ComponentNotFinite_Throws(float component) {
        Assert.Throws<InvalidOperationException>(() => EmbeddingVector.Normalize([1f, component]));
    }


    [Test, Parallelizable]
    [Description("A vector without length can not be scaled and is refused.")]
    public void Normalize_ZeroVector_Throws() {
        Assert.Throws<InvalidOperationException>(() => EmbeddingVector.Normalize([0f, 0f]));
    }


    [Test, Parallelizable]
    [Description("An output which is not one row of 768 components is refused with the shape it had.")]
    [TestCase(new[] { 1, 512 }, "[1, 512]")]
    [TestCase(new[] { 2, 768 }, "[2, 768]")]
    [TestCase(new[] { 768 }, "[768]")]
    [TestCase(new[] { 1, 768, 1 }, "[1, 768, 1]")]
    public void FromModelOutput_WrongShape_ThrowsNamingObservedShape(int[] shape, string described) {
        InvalidOperationException? exception = Assert.Throws<InvalidOperationException>(() => EmbeddingVector.FromModelOutput(shape, Enumerable.Repeat(1f, shape.Aggregate(1, (product, size) => product * size)).ToArray()));

        Assert.Multiple(() => {
            Assert.That(exception!.Message, Does.Contain(described));
            Assert.That(exception.Message, Does.Contain("[1, 768]"));
        });
    }


    [Test, Parallelizable]
    [Description("An output of one row of 768 components is scaled to length one.")]
    public void FromModelOutput_ExpectedShape_ReturnsUnitVector() {
        float[] output = Enumerable.Repeat(2f, EmbeddingExpression.Dimensions).ToArray();

        float[] vector = EmbeddingVector.FromModelOutput([1, EmbeddingExpression.Dimensions], output);

        Assert.That(vector, Is.EqualTo(EmbeddingVector.Normalize(output)));
    }
}
