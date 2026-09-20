using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Sharing;
using DotNetLab.Infrastructure.Worker;
using DotNetLab.Lab;

namespace DotNetLab.Features.Documents;

public sealed class LabFormatter(
    LabDocuments documents,
    LabLanguageSession language,
    WorkerHost worker,
    LabPersistence persist)
{
    public async Task FormatActiveSource()
    {
        var fileName = documents.ActiveSource;
        if (!documents.Sources.TryGetValue(fileName, out var currentCode))
        {
            return;
        }

        if (!fileName.IsCSharpFileName(out var isScript) &&
            fileName != LabFixtures.ConfigurationFileName)
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
