namespace Backend.Services.Auth;

/// <summary>
/// generator for random keys
/// </summary>
public interface IKeyGenerator {
	
	/// <summary>
	/// generates a random key of given length
	/// </summary>
	/// <param name="length">count of random integers to turn into key</param>
	/// <remarks>output key length is <see cref="length"/> * 5</remarks>
	/// <returns>generated key</returns>
	public string GenerateKey(int length);
}