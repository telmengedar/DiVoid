using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models.Auth;

/// <summary>
/// key used to access api from external systems
/// </summary>
[AllowPatch]
public class ApiKey {
	
	/// <summary>
	/// id of key
	/// </summary>
	[PrimaryKey, AutoIncrement]
	public long Id { get; set; }

	/// <summary>
	/// customer for which key is valid
	/// </summary>
	[AllowPatch]
	public long UserId { get; set; }
	
	/// <summary>
	/// key data
	/// </summary>
	[Index("key")]
	public string Key { get; set; }

	/// <summary>
	/// permissions included in key
	/// </summary>
	[AllowPatch]
	public string Permissions { get; set; }
}