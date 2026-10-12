using Microsoft.AspNetCore.Mvc.ModelBinding;
using Pooshit.AspNetCore.Services.Convert;

namespace mamgo.services.Binding; 

/// <summary>
/// binds array parameters
/// </summary>
public class ArrayParameterBinder : IModelBinder {

    /// <inheritdoc />
    public Task BindModelAsync(ModelBindingContext bindingContext) {
        ValueProviderResult valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (valueResult == ValueProviderResult.None)
            return Task.CompletedTask;

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);
        if (valueResult.Values.Count == 0)
            return Task.CompletedTask;

        string[] tokens;
        if (valueResult.Values.Count > 1)
            tokens = valueResult.Values.ToArray();
        else {
            string stringValue = valueResult.FirstValue;
            if (string.IsNullOrEmpty(stringValue))
                return Task.CompletedTask;

            bool delimited = stringValue.StartsWith('{') && stringValue.EndsWith('}') || stringValue.StartsWith('[') && stringValue.EndsWith(']');
            tokens = (delimited ? stringValue.Substring(1, stringValue.Length - 2) : stringValue).Split(',');
        }

        Type elementType = bindingContext.ModelType.GetElementType();
        Array value = Array.CreateInstance(elementType, tokens.Length);
        for (int i = 0; i < tokens.Length; ++i)
            value.SetValue(ConvertToken(tokens[i], elementType, bindingContext.ModelName), i);

        bindingContext.Result = ModelBindingResult.Success(value);
        return Task.CompletedTask;
    }

    static object ConvertToken(string token, Type elementType, string parameter) {
        try {
            return Converter.Convert(token, elementType);
        }
        catch (Exception e) when (e is FormatException or OverflowException) {
            throw new ArgumentException($"'{token}' is not a valid value for {parameter}", e);
        }
    }
}