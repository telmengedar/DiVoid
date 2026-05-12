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

    static string ResolveJsonColumnValue(object value, Type entitytype, PropertyInfo property) {
        if (value is object[] arr) {
            string[] strings = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] is string s)
                    strings[i] = s;
                else
                    throw new ArgumentException(
                        $"'{entitytype.Name}::{property.Name}' must be an array of strings; "
                        + $"element at index {i} has type {arr[i]?.GetType().Name ?? "null"}.");
            }
            return Json.WriteString(strings);
        }
        if (value is string str)
            return str;
        if (value is null)
            return null;
        throw new ArgumentException(
            $"'{entitytype.Name}::{property.Name}' expects a JSON array or pre-encoded JSON string, "
            + $"got {value.GetType().Name}.");
    }
}
