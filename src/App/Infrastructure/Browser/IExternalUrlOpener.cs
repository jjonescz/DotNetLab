using Microsoft.JSInterop;

namespace DotNetLab.Infrastructure.Browser;

public interface IExternalUrlOpener
{
    Task OpenAsync(string url);
}

public sealed class JsExternalUrlOpener(IJSRuntime js) : IExternalUrlOpener
{
    public Task OpenAsync(string url)
        => js.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer").AsTask();
}
