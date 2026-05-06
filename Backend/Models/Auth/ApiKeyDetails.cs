namespace Backend.Models.Auth;

/// <summary>
/// full api key information
/// </summary>
public class ApiKeyDetails {
	
	/// <summary>
	/// id of key
	/// </summary>
	public long Id { get; set; }

	/// <summary>
	/// user for which key is valid
	/// </summary>
	public long? UserId { get; set; }
	
	/// <summary>
	/// key data
	/// </summary>
	public string Key { get; set; }

	/// <summary>
	/// permissions included in key
	/// </summary>
	public string[] Permissions { get; set; }
}