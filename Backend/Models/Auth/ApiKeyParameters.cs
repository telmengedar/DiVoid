namespace Backend.Models.Auth;

/// <summary>
/// parameters used to generate api key
/// </summary>
public class ApiKeyParameters {
	
	/// <summary>
	/// id of user linked to api key
	/// </summary>
	public long? UserId { get; set; }
	
	/// <summary>
	/// permissions of api key
	/// </summary>
	public string[] Permissions { get; set; }
}