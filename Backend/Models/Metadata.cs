using Pooshit.Ocelot.Entities.Attributes;

namespace Backend.Models;

/// <summary>
/// fact about this database stored as a name/value pair, declared in <see cref="MetadataKeys"/>
/// </summary>
public class Metadata {

    /// <summary>
    /// name of the fact, unique per database
    /// </summary>
    [PrimaryKey, Size(128)]
    public string Name { get; set; }

    /// <summary>
    /// value of the fact, formatted and parsed by the feature owning <see cref="Name"/>
    /// </summary>
    public string Value { get; set; }
}
