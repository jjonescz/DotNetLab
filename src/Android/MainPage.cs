using Microsoft.AspNetCore.Components.WebView.Maui;

namespace DotNetLab;

public sealed class MainPage : ContentPage
{
    public MainPage()
    {
        var blazorWebView = new BlazorWebView
        {
            HostPage = "wwwroot/index.html",
        };
        blazorWebView.BlazorWebViewInitialized += (_, args) =>
        {
            args.WebView.AddJavascriptInterface(new AndroidClipboardBridge(), AndroidClipboardBridge.Name);
        };

        App.RegisterRootComponents((componentType, selector) =>
        {
            blazorWebView.RootComponents.Add(new RootComponent
            {
                ComponentType = componentType,
                Selector = selector,
            });
        });

        Content = blazorWebView;
    }
}
