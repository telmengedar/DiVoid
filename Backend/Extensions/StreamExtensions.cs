namespace Backend.Extensions;

/// <summary>
/// extension methods for streams
/// </summary>
public static class StreamExtensions
{

    /// <summary>
    /// converts a stream to a memory stream
    /// </summary>
    /// <param name="stream">stream to convert</param>
    /// <returns>converted stream</returns>
    public static async Task<MemoryStream> ToMemoryStream(this Stream stream)
    {
        if (stream is MemoryStream ms)
            return ms;

        ms = new();
        await stream.CopyToAsync(ms);
        return ms;
    }

    /// <summary>
    /// converts a stream to a byte array
    /// </summary>
    /// <param name="stream">stream to convert</param>
    /// <returns>data of stream</returns>
    public static async Task<byte[]> ToByteArray(this Stream stream)
    {
        MemoryStream memory = await ToMemoryStream(stream);
        return memory.ToArray();
    }
}