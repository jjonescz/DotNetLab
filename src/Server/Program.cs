using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseBlazorFrameworkFiles();

var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".pdb"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });

app.MapFallbackToFile("index.html");

app.Run();
