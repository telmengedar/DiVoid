using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens;
using Pooshit.Ocelot.Tokens.Values;

namespace Backend.Services.Embeddings;

/// <summary>
/// vectors computed in .NET and handed to Postgres
/// </summary>
public static class EmbeddingVector {

    /// <summary>
    /// scales a vector to length one
    /// </summary>
    /// <param name="vector">model output</param>
    /// <returns>normalised copy</returns>
    /// <exception cref="InvalidOperationException">the vector has a component which is not finite, or no length</exception>
    public static float[] Normalize(float[] vector) {
        double sum = 0;
        foreach (float component in vector) {
            if (!float.IsFinite(component))
                throw new InvalidOperationException("embedding model returned a component which is not finite");
            sum += (double) component * component;
        }

        if (sum == 0)
            throw new InvalidOperationException("embedding model returned a vector of length zero");

        float length = (float) Math.Sqrt(sum);
        return vector.Select(component => component / length).ToArray();
    }

    /// <summary>
    /// checks the shape of the embedding the model returned for one prompt and scales it to length one
    /// </summary>
    /// <param name="shape">dimensions of the model output</param>
    /// <param name="components">values of the model output</param>
    /// <returns>vector of length one with <see cref="EmbeddingExpression.Dimensions"/> components</returns>
    /// <exception cref="InvalidOperationException">the shape is not one row of <see cref="EmbeddingExpression.Dimensions"/> components, or the vector can not be scaled</exception>
    public static float[] FromModelOutput(int[] shape, float[] components) {
        if (shape is not [1, EmbeddingExpression.Dimensions])
            throw new InvalidOperationException($"embedding model returned shape [{string.Join(", ", shape)}] instead of [1, {EmbeddingExpression.Dimensions}]");

        return Normalize(components);
    }

    /// <summary>
    /// builds the expression which evaluates to a constant vector
    /// </summary>
    /// <param name="vector">components of the vector</param>
    /// <returns>vector expression usable for column writes and comparisons</returns>
    public static ISqlToken ToExpression(float[] vector) {
        return DB.Value<object>(v => DB.Cast(DB.Constant(vector), CastType.Vector));
    }
}
