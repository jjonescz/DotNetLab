using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.Loader;

namespace DotNetLab;

public static class Executor
{
    public static string Execute(MemoryStream emitStream)
    {
        var alc = new AssemblyLoadContext(nameof(Executor));
        try
        {
            var assembly = alc.LoadFromStream(emitStream);

            var entryPoint = assembly.EntryPoint
                ?? throw new ArgumentException("No entry point found in the assembly.");

            int exitCode = 0;
            Util.CaptureConsoleOutput(
                () =>
                {
                    exitCode = InvokeEntryPoint(entryPoint);
                },
                out string stdout, out string stderr);

            return $"Exit code: {exitCode}\nStdout:\n{stdout}\nStderr:\n{stderr}";
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
    }

    public static int InvokeEntryPoint(MethodInfo entryPoint)
    {
        var parameters = entryPoint.GetParameters().Length == 0
            ? null
            : new object[] { Array.Empty<string>() };
        return entryPoint.Invoke(null, parameters) is int e ? e : 0;
    }

    public static async Task<string> RenderComponentToHtmlAsync(MemoryStream emitStream, string componentTypeName)
    {
        var alc = new AssemblyLoadContext(nameof(RenderComponentToHtmlAsync));
        var assembly = alc.LoadFromStream(emitStream);
        var componentType = assembly.GetType(componentTypeName)
            ?? throw new InvalidOperationException($"Cannot find component '{componentTypeName}' in the assembly.");

        var services = new ServiceCollection();
        services.AddLogging();
        var serviceProvider = services.BuildServiceProvider();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var renderer = new HtmlRenderer(serviceProvider, loggerFactory);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync(componentType);
            return output.ToHtmlString();
        });
        return html;
    }

    public static async Task<string> RenderRazorPageToHtmlAsync(MemoryStream emitStream, string pageTypeName)
    {
        var alc = new AssemblyLoadContext(nameof(RenderRazorPageToHtmlAsync));
        var assembly = alc.LoadFromStream(emitStream);
        var pageType = assembly.GetType(pageTypeName)
            ?? throw new InvalidOperationException($"Cannot find page '{pageTypeName}' in the assembly.");

        var page = (RazorPageBase)Activator.CreateInstance(pageType)!;

        // Create ViewContext.
        var appBuilder = WebApplication.CreateBuilder();
        appBuilder.Services.AddMvc().ConfigureApplicationPartManager(manager =>
        {
            var partFactory = new ConsolidatedAssemblyApplicationPartFactory();
            foreach (var applicationPart in partFactory.GetApplicationParts(assembly))
            {
                manager.ApplicationParts.Add(applicationPart);
            }
        });
        var app = appBuilder.Build();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        var requestFeature = new HttpRequestFeature
        {
            Method = HttpMethods.Get,
            Protocol = HttpProtocol.Http2,
            Scheme = "http"
        };
        requestFeature.Headers.Host = "localhost";
        httpContext.Features.Set<IHttpRequestFeature>(requestFeature);
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor());
        var tempDataProvider = new TestTempDataProvider();
        var tempDataFactory = new TempDataDictionaryFactory(tempDataProvider);
        var viewStarts = getViewStartNames(pageType.Name)
            .Select(n => assembly.GetType(string.IsNullOrEmpty(pageType.Namespace) ? n : $"{pageType.Namespace}.{n}"))
            .Where(t => t is not null)
            .Select(t => (IRazorPage)Activator.CreateInstance(t!)!)
            .ToImmutableArray();
        var view = ActivatorUtilities.CreateInstance<RazorView>(app.Services,
            /* IReadOnlyList<IRazorPage> viewStartPages */ viewStarts,
            /* IRazorPage razorPage */ page);
        var writer = new StringWriter();
        var viewContext = new ViewContext(
            actionContext,
            view,
            new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            tempDataFactory.GetTempData(httpContext),
            writer,
            new HtmlHelperOptions());

        // Render the page.
        await view.RenderAsync(viewContext);

        return writer.ToString();

        // Inspired by Microsoft.AspNetCore.Mvc.Razor.RazorFileHierarchy.GetViewStartPaths.
        static IEnumerable<string> getViewStartNames(string name)
        {
            var builder = new StringBuilder(name);
            var index = name.Length;
            for (var currentIteration = 0; currentIteration < 255; currentIteration++)
            {
                if (index <= 1 || (index = name.LastIndexOf('_', index - 1)) < 0)
                {
                    break;
                }

                builder.Length = index + 1;
                builder.Append("_ViewStart");

                var itemPath = builder.ToString();
                yield return itemPath;
            }
        }
    }
}

internal sealed class TestTempDataProvider : ITempDataProvider
{
    private readonly Dictionary<string, object> data = new();

    public IDictionary<string, object> LoadTempData(HttpContext context)
    {
        return data;
    }

    public void SaveTempData(HttpContext context, IDictionary<string, object> values)
    {
    }
}
