using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RoomBooking.Api.Infrastructure;

public sealed class ExplicitOffsetTimestampModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        var values = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
        if (values == ValueProviderResult.None)
        {
            bindingContext.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        bindingContext.ModelState.SetModelValue(bindingContext.ModelName, values);
        if (values.Length != 1 || !ExplicitOffsetTimestamp.TryParse(values.FirstValue, out var value))
        {
            bindingContext.ModelState.TryAddModelError(
                bindingContext.ModelName, ExplicitOffsetTimestamp.ErrorMessage);
            bindingContext.Result = ModelBindingResult.Failed();
        }
        else
        {
            bindingContext.Result = ModelBindingResult.Success(value);
        }

        return Task.CompletedTask;
    }
}
