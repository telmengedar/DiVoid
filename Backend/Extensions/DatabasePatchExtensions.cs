using System.Linq.Expressions;
using System.Reflection;
using Pooshit.AspNetCore.Services.Convert;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Tokens;

namespace Backend.Extensions; 

/// <summary>
/// extensions for patch operations in a database context
/// </summary>
public static class DatabasePatchExtensions {

    /// <summary>
    /// applies a set of patch operations to an update operation
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="updateoperation">update operation to be updated</param>
    /// <param name="operations">operations to apply</param>
    /// <returns>the update operation for fluent behavior</returns>
    public static UpdateValuesOperation<T> Patch<T>(this UpdateValuesOperation<T> updateoperation, params PatchOperation[] operations) {
        return Patch(updateoperation, (IEnumerable<PatchOperation>) operations);
    }
    
    /// <summary>
    /// applies a set of patch operations to an update operation
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="updateoperation">update operation to be updated</param>
    /// <param name="operations">operations to apply</param>
    /// <returns>the update operation for fluent behavior</returns>
    public static UpdateValuesOperation<T> Patch<T>(this UpdateValuesOperation<T> updateoperation, IEnumerable<PatchOperation> operations) {
        Type entitytype = typeof(T);

        List<Expression<Func<T, bool>>> setters = [];
        foreach(PatchOperation patch in operations) {
            string propertyname = patch.Path[1..].ToLower();
            PropertyInfo property = entitytype.GetProperties().FirstOrDefault(p => p.Name.ToLower() == propertyname);
            if(property == null)
                throw new PropertyNotFoundException(entitytype, propertyname);

            if(!Attribute.IsDefined(property, typeof(AllowPatchAttribute)))
                throw new NotSupportedException($"Patching of '{entitytype.Name}::{property.Name}' is not supported");

            object targetvalue = null;
            if (patch.Op != "embed")
                targetvalue = Converter.Convert(patch.Value, property.PropertyType, true);
            
            switch (patch.Op) {
                case Pooshit.AspNetCore.Services.Patches.Patch.Op_Replace:
                    setters.Add(e => DB.Property<T>(property.Name, true) == targetvalue);
                    break;
                case Pooshit.AspNetCore.Services.Patches.Patch.Op_Add:
                    setters.Add(e => DB.Property<T>(property.Name, true).Decimal == DB.Property<T>(property.Name, true).Decimal + DB.Constant(targetvalue).Decimal);
                    break;
                case Pooshit.AspNetCore.Services.Patches.Patch.Op_Remove:
                    setters.Add(e => DB.Property<T>(property.Name, true).Decimal == DB.Property<T>(property.Name, true).Decimal - DB.Constant(targetvalue).Decimal);
                    break;
                case Pooshit.AspNetCore.Services.Patches.Patch.Op_Flag:
                    setters.Add(e => DB.Property<T>(property.Name, true).Int64 == (DB.Property<T>(property.Name, true).Int64 | DB.Constant(targetvalue).Int64));
                break;
                case Pooshit.AspNetCore.Services.Patches.Patch.Op_Unflag:
                    setters.Add(e => DB.Property<T>(property.Name, true).Int64 == (DB.Property<T>(property.Name, true).Int64 & ~DB.Constant(targetvalue).Int64));
                break;
                case "embed":
                    setters.Add(e => DB.Property<T>(property.Name, true) == DB.CustomFunction("embedding", DB.Constant("gemini-embedding-001"), DB.Constant(patch.Value)));
                break;
                default:
                    throw new ArgumentException($"Unsupported patch operation '{patch.Op}'");
            }
        }

        if (setters.Count > 0)
            updateoperation.Set(setters.ToArray());
        return updateoperation;
    }
}