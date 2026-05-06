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

        Array value;
        Type elementType = bindingContext.ModelType.GetElementType();
        if (valueResult.Values.Count > 1) {
            value = Array.CreateInstance(elementType, valueResult.Values.Count);
            for (int i = 0; i < valueResult.Values.Count; ++i) {
                object converted = Converter.Convert(valueResult.Values[i], elementType);
                value.SetValue(converted, i);
            }

            bindingContext.Result = ModelBindingResult.Success(value);
            return Task.CompletedTask;
        }

        string stringValue = valueResult.FirstValue;
        if (string.IsNullOrEmpty(stringValue))
            return Task.CompletedTask;

        string[] array;
        if (stringValue.StartsWith('{') && stringValue.EndsWith('}') || stringValue.StartsWith('[') && stringValue.EndsWith(']')) {
            array = stringValue.Substring(1, stringValue.Length - 2).Split(',');
            value = Array.CreateInstance(elementType, array.Length);
            for (int i = 0; i < array.Length; ++i)
                value.SetValue(Converter.Convert(array[i], elementType), i);
            bindingContext.Result = ModelBindingResult.Success(value);
            return Task.CompletedTask;
        }

        array = stringValue.Split(',');
        value = Array.CreateInstance(elementType, array.Length);
        for (int i = 0; i < array.Length; ++i)
            value.SetValue(Converter.Convert(array[i], elementType), i);
        bindingContext.Result = ModelBindingResult.Success(value);
        return Task.CompletedTask;
    }
}