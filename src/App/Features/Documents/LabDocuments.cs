using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Compilation;
using DotNetLab.Features.Outputs;
using DotNetLab.Lab;
using Fluxor;

namespace DotNetLab.Features.Documents;

public sealed class LabDocuments
{
    private readonly IDispatcher _dispatcher;
    private readonly IState<CompilationState> _compilation;
    private readonly IState<OutputState> _output;
    private readonly Lazy<LabLanguageSession>? _language;
    private readonly Lazy<OutputTabLayout>? _tabs;
    private readonly Lazy<OutputSession>? _outputs;
    private readonly Lazy<CompilationSession>? _compilationSession;
    private readonly Dictionary<string, string> _modelUris = new(StringComparer.Ordinal);

    internal LabDocuments(
        IDispatcher dispatcher,
        IState<CompilationState> compilation,
        IState<OutputState> output,
        Lazy<LabLanguageSession>? language = null,
        Lazy<OutputTabLayout>? tabs = null,
        Lazy<OutputSession>? outputs = null,
        Lazy<CompilationSession>? compilationSession = null)
    {
        _dispatcher = dispatcher;
        _compilation = compilation;
        _output = output;
        _language = language;
        _tabs = tabs;
        _outputs = outputs;
        _compilationSession = compilationSession;
        EnsureUri(InitialCode.CSharp.SuggestedFileName);
    }

    public event Action? Changed;

    public event Func<Task>? PersistUrlRequested;

    public string Template { get; private set; } = "C#";
    public string ActiveSource { get; set; } = "Program.cs";

    public IReadOnlyDictionary<string, string> Sources => _sources;
    public IReadOnlyList<string> SourceFiles => _sourceFiles;

    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal)
    {
        [InitialCode.CSharp.SuggestedFileName] = InitialCode.CSharp.TextTemplate,
    };

    private readonly List<string> _sourceFiles = ["Program.cs"];

    public string UriFor(string fileName)
    {
        if (!_modelUris.TryGetValue(fileName, out var uri))
        {
            uri = CompiledAssembly.GetInputModelUri(fileName);
            _modelUris[fileName] = uri;
        }

        return uri;
    }

    public IReadOnlyList<string> ModelUris => _modelUris.Values.ToArray();

    public string? RemoveUri(string fileName)
    {
        _modelUris.Remove(fileName, out var uri);
        return uri;
    }

    public IReadOnlyList<string> ResetUris()
    {
        var uris = _modelUris.Values.ToArray();
        _modelUris.Clear();
        return uris;
    }

    public ImmutableArray<ModelInfo> CreateModelInfos()
        => SourceFiles
            .Select(file => new ModelInfo(UriFor(file), file)
            {
                NewContent = _sources.GetValueOrDefault(file) ?? "",
                IsConfiguration = file == LabFixtures.ConfigurationFileName,
            })
            .ToImmutableArray();

    private void EnsureUri(string fileName) => UriFor(fileName);

    public static bool IsSpecialSource(string fileName)
        => fileName is LabFixtures.DirectivesFileName or LabFixtures.ConfigurationFileName;

    public static string DisplayName(string fileName)
        => fileName switch
        {
            LabFixtures.DirectivesFileName => "Directives",
            LabFixtures.ConfigurationFileName => "Configuration",
            _ => fileName
        };

    public void SetTemplate(string template)
    {
        var before = ModelUris;
        Template = template;

        foreach (var file in _sourceFiles.Where(name => !IsSpecialSource(name)).ToArray())
        {
            _sourceFiles.Remove(file);
            _sources.Remove(file);
            RemoveUri(file);
        }

        foreach (var (name, contents) in FilesFor(template))
        {
            InsertUserFile(name);
            _sources[name] = contents;
            EnsureUri(name);
        }

        if (template is "Razor")
        {
            ActiveSource = "TestComponent.razor";
        }
        else if (template is "CSHTML")
        {
            ActiveSource = "TestPage.cshtml";
        }
        else
        {
            ActiveSource = "Program.cs";
        }

        SetActiveOutput(template is "Razor" or "CSHTML" ? "gcs" : "cs");
        Stale = true;
        EnsureActiveOutput();
        Notify();
        AfterChanged(before);
    }

    private void AfterChanged(IReadOnlyList<string> before)
    {
        _ = _language?.Value.AfterDocumentsChangedAsync(before) ?? Task.CompletedTask;
        _ = RequestPersistUrlAsync();
    }

    private static (string Name, string Contents)[] FilesFor(string template)
        => template switch
        {
            "Razor" =>
            [
                (InitialCode.Razor.SuggestedFileName, InitialCode.Razor.TextTemplate),
                (InitialCode.RazorImports.SuggestedFileName, InitialCode.RazorImports.TextTemplate),
            ],
            "CSHTML" =>
            [
                (InitialCode.Cshtml.SuggestedFileName, InitialCode.Cshtml.TextTemplate),
            ],
            _ =>
            [
                (InitialCode.CSharp.SuggestedFileName, InitialCode.CSharp.TextTemplate),
            ],
        };

    public void SetSource(string file, string contents)
    {
        _sources[file] = contents;
        if (Stale)
        {
            return;
        }

        Stale = true;
    }

    public void RenameFile(string oldName, string newName)
    {
        if (!TryNormalizeRename(oldName, newName, out var normalized))
        {
            return;
        }

        var index = _sourceFiles.IndexOf(oldName);
        if (index < 0)
        {
            return;
        }

        var before = ModelUris;
        _sourceFiles[index] = normalized;
        if (_sources.Remove(oldName, out var contents))
        {
            _sources[normalized] = contents;
        }

        RemoveUri(oldName);
        EnsureUri(normalized);

        if (string.Equals(ActiveSource, oldName, StringComparison.Ordinal))
        {
            ActiveSource = normalized;
            EnsureActiveOutput();
        }

        Stale = true;
        Notify();
        AfterChanged(before);
    }

    private bool TryNormalizeRename(string oldName, string newName, out string normalized)
    {
        normalized = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(normalized) ||
            string.Equals(oldName, normalized, StringComparison.Ordinal) ||
            IsSpecialSource(oldName) ||
            IsSpecialSource(normalized) ||
            normalized is "." or ".." ||
            normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(normalized), normalized, StringComparison.Ordinal) ||
            !_sourceFiles.Contains(oldName) ||
            _sourceFiles.Contains(normalized))
        {
            normalized = "";
            return false;
        }

        return true;
    }

    public void CloseFile(string file)
    {
        if (_sourceFiles.Count <= 1 || !_sourceFiles.Remove(file))
        {
            return;
        }

        var before = ModelUris;
        _sources.Remove(file);
        RemoveUri(file);
        if (ActiveSource == file)
        {
            ActiveSource = _sourceFiles.FirstOrDefault(name => !IsSpecialSource(name)) ?? _sourceFiles[0];
            EnsureActiveOutput();
        }

        Stale = true;
        Notify();
        AfterChanged(before);
    }

    public void AddFile(string extension)
    {
        var index = 1;
        string name;
        do
        {
            name = extension switch
            {
                ".razor" => $"Component{index}.razor",
                ".cshtml" => $"Page{index}.cshtml",
                _ => $"File{index}.cs"
            };
            index++;
        } while (_sourceFiles.Contains(name));

        var contents = extension switch
        {
            ".razor" => $"<h1>{name}</h1>\n",
            ".cshtml" => $"@page \"/{Path.GetFileNameWithoutExtension(name).ToLowerInvariant()}\"\n\n<h1>{name}</h1>\n",
            _ => $"public class {Path.GetFileNameWithoutExtension(name)}\n{{\n}}\n"
        };

        var before = ModelUris;
        InsertUserFile(name);
        _sources[name] = contents;
        EnsureUri(name);
        ActiveSource = name;
        EnsureActiveOutput();
        Stale = true;
        Notify();
        AfterChanged(before);
    }

    public void OpenDirectives() => OpenSpecialSource(InitialCode.Directives.SuggestedFileName, InitialCode.Directives.TextTemplate);

    public void OpenConfiguration() => OpenSpecialSource(InitialCode.Configuration.SuggestedFileName, InitialCode.Configuration.TextTemplate);

    private static bool IsUserCsharpFile(string fileName)
        => fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsSpecialSource(fileName);

    private void OpenSpecialSource(string fileName, string contents)
    {
        var before = ModelUris;
        if (!_sourceFiles.Contains(fileName))
        {
            InsertSpecialFile(fileName);
            _sources[fileName] = contents;
            EnsureUri(fileName);
            Stale = true;
        }

        ActiveSource = fileName;
        EnsureActiveOutput();
        Notify();
        AfterChanged(before);
    }

    private void InsertUserFile(string name)
    {
        var at = _sourceFiles.FindIndex(IsSpecialSource);
        if (at < 0)
        {
            _sourceFiles.Add(name);
            return;
        }

        _sourceFiles.Insert(at, name);
    }

    private void InsertSpecialFile(string fileName)
    {
        var userCount = _sourceFiles.FindIndex(IsSpecialSource);
        if (userCount < 0)
        {
            userCount = _sourceFiles.Count;
        }

        var slot = Array.IndexOf(LabFixtures.SpecialSourceOrder, fileName);
        var at = userCount;
        for (var i = 0; i < slot; i++)
        {
            if (_sourceFiles.Contains(LabFixtures.SpecialSourceOrder[i]))
            {
                at++;
            }
        }

        _sourceFiles.Insert(at, fileName);
    }

    public void LoadImportedFiles(IReadOnlyDictionary<string, string> files)
    {
        var incoming = files
            .Select(pair => (Name: Path.GetFileName(pair.Key.Trim()), Contents: pair.Value ?? ""))
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.Name) &&
                pair.Name is not "." and not ".." &&
                pair.Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            .GroupBy(pair => pair.Name, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();

        if (incoming.Length == 0)
        {
            return;
        }

        var before = ModelUris;
        foreach (var file in _sourceFiles.Where(name => !IsSpecialSource(name)).ToArray())
        {
            _sourceFiles.Remove(file);
            _sources.Remove(file);
            RemoveUri(file);
        }

        foreach (var (name, contents) in incoming)
        {
            if (IsSpecialSource(name))
            {
                if (!_sourceFiles.Contains(name))
                {
                    InsertSpecialFile(name);
                }

                _sources[name] = contents;
                EnsureUri(name);
                continue;
            }

            InsertUserFile(name);
            _sources[name] = contents;
            EnsureUri(name);
        }

        ActiveSource = _sourceFiles.FirstOrDefault(name => !IsSpecialSource(name)) ?? _sourceFiles[0];
        EnsureActiveOutput();
        Stale = true;
        Notify();
        AfterChanged(before);
    }

    public void SetActiveSource(string file)
    {
        if (string.Equals(ActiveSource, file, StringComparison.Ordinal))
        {
            return;
        }

        ActiveSource = file;
        EnsureActiveOutput();
        Notify();
        _ = _language?.Value.SyncAsync() ?? Task.CompletedTask;
        _compilationSession?.Value.RefreshTemporaryErrorList();
        _ = _outputs?.Value.LoadDisplayedAsync() ?? Task.CompletedTask;
        _ = RequestPersistUrlAsync();
    }

    public void LoadFromSavedState(SavedState state)
    {
        var userFiles = new List<(string Name, string Contents)>();
        var specialFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in state.Inputs)
        {
            var name = string.IsNullOrWhiteSpace(input.FileName)
                ? InitialCode.CSharp.SuggestedFileName
                : Path.GetFileName(input.FileName);
            if (string.IsNullOrWhiteSpace(name) ||
                name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                continue;
            }

            var text = input.Text ?? "";
            if (IsSpecialSource(name))
            {
                specialFiles[name] = text;
            }
            else
            {
                userFiles.Add((name, text));
            }
        }

        if (state.Configuration is { } configuration)
        {
            specialFiles[LabFixtures.ConfigurationFileName] = configuration;
        }

        _sourceFiles.Clear();
        _sources.Clear();
        ResetUris();

        if (userFiles.Count == 0)
        {
            userFiles.Add((InitialCode.CSharp.SuggestedFileName, InitialCode.CSharp.TextTemplate));
        }

        foreach (var (name, contents) in userFiles)
        {
            if (!_sourceFiles.Contains(name))
            {
                InsertUserFile(name);
            }

            _sources[name] = contents;
        }

        foreach (var fileName in LabFixtures.SpecialSourceOrder)
        {
            if (specialFiles.TryGetValue(fileName, out var contents))
            {
                InsertSpecialFile(fileName);
                _sources[fileName] = contents;
            }
        }

        var selectable = _sourceFiles.Where(name => name != LabFixtures.ConfigurationFileName).ToList();
        if (selectable.Count == 0)
        {
            selectable = _sourceFiles.ToList();
        }

        ActiveSource = state.SelectedInputIndex >= 0 && state.SelectedInputIndex < selectable.Count
            ? selectable[state.SelectedInputIndex]
            : selectable[0];

        Template = selectable.Any(file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            ? "Razor"
            : selectable.Any(file => file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                ? "CSHTML"
                : "C#";

        EnsureActiveOutput();
        Notify();
    }

    private void Notify()
    {
        _dispatcher.Dispatch(new SetDocumentMetadataAction(
            Template,
            ActiveSource,
            [.. SourceFiles]));
        Changed?.Invoke();
    }

    private bool Stale
    {
        get => _compilation.Value.Stale;
        set => _dispatcher.Dispatch(new SetStaleAction(value));
    }

    private void SetActiveOutput(string type)
    {
        if (_outputs is not null)
        {
            var dismiss = _outputs.Value.DismissTemporaryErrorList();
            if (string.Equals(_output.Value.ActiveOutput, type, StringComparison.Ordinal))
            {
                if (dismiss)
                {
                    Notify();
                }

                return;
            }

            _dispatcher.Dispatch(new SetActiveOutputAction(type));
            _ = _outputs.Value.EnsureOutputLoadedAsync(type);
            return;
        }

        _dispatcher.Dispatch(new SetActiveOutputAction(type));
    }

    private void EnsureActiveOutput() => _tabs?.Value.EnsureActiveOutput();

    private Task RequestPersistUrlAsync()
    {
        var handlers = PersistUrlRequested;
        if (handlers is null)
        {
            return Task.CompletedTask;
        }

        return InvokeHandlersAsync(handlers);
    }

    private static async Task InvokeHandlersAsync(Func<Task> handlers)
    {
        foreach (var handler in handlers.GetInvocationList())
        {
            await ((Func<Task>)handler)();
        }
    }
}
