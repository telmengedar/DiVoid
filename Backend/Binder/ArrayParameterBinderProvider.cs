using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

namespace mamgo.services.Binding; 

/// <summary>
/// provides a parameter binder for array types
/// </summary>
public class ArrayParameterBinderProvider : IModelBinderProvider {
    /// <inheritdoc />
    public IModelBinder GetBinder(ModelBinderProviderContext context) {
        if (context.Metadata.ModelType.IsArray && (context.Metadata.BindingSource==BindingSource.Query || context.Metadata.ContainerMetadata!=null))
            return new BinderTypeModelBinder(typeof(ArrayParameterBinder));
        return null;
    }
}