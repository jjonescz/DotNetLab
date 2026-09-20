using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Sharing;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;

namespace DotNetLab.Features.Documents;

public sealed class DocumentFormatter(
    DocumentWorkspace documents,
    LabLanguageSession language,
    WorkerHost worker,
    AppPersistence persist)
{
    public async Task FormatActiveDocument()
    {
        var fileName = documents.ActiveDocument;
        if (!documents.Sources.TryGetValue(fileName, out var currentCode))
        {
            return;
        }

        if (!fileName.IsCSharpFileName(out var isScript) &&
            fileName != BuiltInContent.ConfigurationFileName)
        {
            return;
        }

        try
        {
            var formatted = await worker.SendAsync(
                new WorkerInputMessage.FormatCode(currentCode, isScript)
                {
                    Id = worker.NextMessageId(),
                });

            if (formatted == currentCode)
            {
                return;
            }

            documents.SetSource(fileName, formatted);
            await language.SyncAsync();
            await persist.PersistUrlAsync();
        }
        catch
        {
            // Same as Lab: formatting is best-effort and should not interrupt editing.
        }
    }
}
