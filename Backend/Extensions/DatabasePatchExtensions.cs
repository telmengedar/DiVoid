using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Backend.Models.Attributes;
using Pooshit.AspNetCore.Services.Convert;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Patches;
using Pooshit.Json;
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
    public static UpdateValuesOperation<T> Patch<T>(this UpdateValuesOperation<T> updateoperation, params PatchOperation[] operations) {
        return Patch(updateoperation, (IEnumerable<PatchOperation>) operations);
    }

    /// <summary>
    /// applies a set of patch operations to an update operation
    /// </summary>
    /// <exception cref="PropertyNotFoundException">
    /// Thrown when a patch path does not resolve to any property on <typeparamref name="T"/>.
    /// Mapped to HTTP 400 by <see cref="Backend.Errors.PropertyNotFoundExceptionHandler"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the resolved property is not tagged <c>[AllowPatch]</c>.
    /// Mapped to HTTP 400 by <see cref="Backend.Errors.NotSupportedExceptionHandler"/>.
    /// </exception>
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
            if (patch.Op != "embed") {
                if (Attribute.IsDefined(property, typeof(JsonColumnAttribute)))
                    targetvalue = ResolveJsonColumnValue(patch.Value, entitytype, property);
                else
                    targetvalue = Converter.Convert(patch.Value, property.PropertyType, true);
            }

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

    /// <summary>
    /// converts a patch value for a <c>[JsonColumn]</c> property into the JSON string
    /// that should be written to the database column.
    /// </summary>
    /// <param name="value">raw patch value — null clears the column; an <see cref="object"/>[] of strings sets it</param>
    /// <param name="entitytype">entity type owning the property (used in error messages)</param>
    /// <param name="property">the <c>[JsonColumn]</c> property being patched</param>
    /// <returns>JSON-encoded string array, or null to clear the column</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="value"/> is not null and not an <see cref="object"/>[], or when any
    /// element of the array is not a <see cref="string"/>. Mapped to HTTP 400 by
    /// <see cref="Backend.Errors.ArgumentExceptionHandler"/>.
    /// </exception>
    static string ResolveJsonColumnValue(object value, Type entitytype, PropertyInfo property) {
        if (value == null)
            return null;

        if (value is not object[] arr)
            throw new ArgumentException(
                $"'{entitytype.Name}::{property.Name}' must be an array; got {value.GetType().Name}.");

        foreach (object el in arr) {
            if (el is not string)
                throw new ArgumentException(
                    $"'{entitytype.Name}::{property.Name}' array elements must be strings; got {el?.GetType().Name ?? "null"}.");
        }

        return Json.WriteString(arr.Cast<string>().ToArray());
    }
}
