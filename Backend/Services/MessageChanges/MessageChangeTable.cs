using Pooshit.Ocelot.Entities.Descriptors;

namespace Backend.Services.MessageChanges;

internal sealed record MessageChangeTable(long Oid, string Schema, string Name, EntityDescriptor Descriptor);
