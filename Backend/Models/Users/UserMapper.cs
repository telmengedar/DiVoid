using Pooshit.Json;
using Pooshit.Ocelot.Fields;

namespace Backend.Models.Users;

/// <summary>
/// mapper for <see cref="User"/>s
/// </summary>
public class UserMapper : FieldMapper<UserDetails, User> {

    /// <summary>
    /// creates a new <see cref="UserMapper"/>
    /// </summary>
    public UserMapper()
        : base(Mappings()) {
    }

    static IEnumerable<FieldMapping<UserDetails>> Mappings() {
        yield return new FieldMapping<UserDetails, long>("id",
                                                         u => u.Id,
                                                         (u, v) => u.Id = v);
        yield return new FieldMapping<UserDetails, string>("name",
                                                           u => u.Name,
                                                           (u, v) => u.Name = v);
        yield return new FieldMapping<UserDetails, string>("email",
                                                           u => u.Email,
                                                           (u, v) => u.Email = v);
        yield return new FieldMapping<UserDetails, bool>("enabled",
                                                         u => u.Enabled,
                                                         (u, v) => u.Enabled = v);
        yield return new FieldMapping<UserDetails, DateTime>("createdat",
                                                              u => u.CreatedAt,
                                                              (u, v) => u.CreatedAt = v);
        yield return new FieldMapping<UserDetails, string>("permissions",
                                                           u => (string)(object)u.Permissions,
                                                           (u, v) => u.Permissions = string.IsNullOrEmpty(v) ? [] : Json.Read<string[]>(v) ?? []);
    }
}
