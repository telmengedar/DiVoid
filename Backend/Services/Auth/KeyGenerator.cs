using System.Security.Cryptography;

namespace Backend.Services.Auth;

/// <inheritdoc />
public class KeyGenerator : IKeyGenerator {
	readonly char[] chars = [
		'W', '9', '6', 'R',
		'X', 'S', '8', '1',
		'7', 'E', 'F', 'T',
		'3', 'U', 'A', '0',
		'M', 'P', '4', 'K',
		'2', 'B', 'L', 'H',
		'Y', 'N', 'D', '5',
		'C', 'I', 'G', 'Z'
	];

	IEnumerable<char> TokenFromNumbers(params int[] numbers) {
		foreach (int number in numbers) {
			yield return chars[number & 0x1F];
			yield return chars[(number >> 5) & 0x1F];
			yield return chars[(number >> 10) & 0x1F];
			yield return chars[(number >> 15) & 0x1F];
			yield return chars[(number >> 20) & 0x1F];
			yield return chars[(number >> 25) & 0x1F];
		}
	}

	/// <inheritdoc />
	public string GenerateKey(int length) {
		RandomNumberGenerator rng = RandomNumberGenerator.Create();
		byte[] buffer=new byte[4];
		List<int> numbers = [];
		for (int i = 0; i < length; ++i) {
			rng.GetBytes(buffer);
			numbers.Add(BitConverter.ToInt32(buffer, 0));
		}

		return new(TokenFromNumbers(numbers.ToArray()).ToArray());
	}
}