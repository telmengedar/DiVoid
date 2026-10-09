using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace mamgo.services.Binding; 

/// <summary>
/// provides a parameter binder for array types
/// </summary>
public class ArrayParameterBinderProvider : IModelBinderProvider {
    /// <inheritdoc />
    public IModelBinder GetBinder(ModelBinderProviderContext context) {
        if (context.Metadata.ModelType.IsArray && (context.Metadata.BindingSource==BindingSource.Query || context.Metadata.ContainerMetadata!=null)) {
            if (IsNoCommaSplit(context.Metadata))
                return null;
            return new BinderTypeModelBinder(typeof(ArrayParameterBinder));
        }
        return null;
    }

    static bool IsNoCommaSplit(ModelMetadata metadata)
        => metadata is DefaultModelMetadata { Attributes.PropertyAttributes: not null } detailed
           && detailed.Attributes.PropertyAttributes.OfType<NoCommaSplitAttribute>().Any();
}