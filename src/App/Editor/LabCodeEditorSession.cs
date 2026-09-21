using BlazorMonaco;
using BlazorMonaco.Editor;
using DotNetLab.Editor.LanguageServices;
using DotNetLab.Features.Compiler;
using DotNetLab.Features.Documents;
using DotNetLab.Features.Preferences;
using DotNetLab.Features.Sharing;
using Fluxor;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace DotNetLab.Editor;

public readonly record struct LabCodeEditorView(
    StandaloneCodeEditor? Editor,
    string Value,
    string Language,
    string? FileName,
    string? ModelUri,
    bool ReadOnly,
    bool WordWrap,
    EventCallback<string> OnValueChanged,
    EventCallback<CursorPositionChangedEvent> OnCursorChanged,
    EventCallback OnFocused);

public sealed class LabCodeEditorSession
{
    private readonly IJSRuntime _js;
    private readonly DocumentWorkspace _documents;
    private readonly AppPersistence _persist;
    private readonly LabEditorSnapshots _snapshots;
    private readonly LabLanguageSession _language;
    private readonly IDispatcher _dispatcher;
    private readonly IState<PreferencesState> _prefs;
    private readonly ILogger<LabCodeEditorSession> _logger;
    private readonly string _editorId = $"lab-editor-{Guid.NewGuid():N}";

    private LabCodeEditorView _view;
    private string _lastValue = "";
    private string _lastLanguage = "";
    private bool _lastReadOnly;
    private bool _lastWordWrap;
    private int _lastCursorLine;
    private int _lastCursorColumn;
    private bool _ready;
    private bool _disposed;
    private bool _suppressChange;
    private Task _init = Task.CompletedTask;
    private Task _applyQueue = Task.CompletedTask;
    private string? _appliedMonacoTheme;
    private bool? _appliedKeyboardDisabled;
    private bool? _appliedVim;
    private string? _lastModelUri;

    public LabCodeEditorSession(
        IJSRuntime js,
        DocumentWorkspace documents,
        AppPersistence persist,
        LabEditorSnapshots snapshots,
        LabLanguageSession language,
        IDispatcher dispatcher,
        IState<PreferencesState> prefs,
        ILogger<LabCodeEditorSession> logger)
    {
        _js = js;
        _documents = documents;
        _persist = persist;
        _snapshots = snapshots;
        _language = language;
        _dispatcher = dispatcher;
        _prefs = prefs;
        _logger = logger;
        _prefs.StateChanged += OnPreferencesChanged;
        _snapshots.Register(FlushAsync);
    }

    public string EditorId => _editorId;

    public string VimStatusId => $"{_editorId}-vim";

    public LabCodeEditorView View
    {
        get => _view;
        set => _view = value;
    }

    // First paint only. Tab switches change Value/Language/ModelUri; applying
    // those through a re-render remounts Monaco instead of swapping the model.
    public bool ShouldRender() => !_ready;

    public StandaloneEditorConstructionOptions ConstructionOptions()
    {
        var view = _view;
        _lastValue = view.Value;
        _lastLanguage = view.Language;
        _lastReadOnly = view.ReadOnly;
        _lastWordWrap = view.WordWrap;
        _lastModelUri = view.ModelUri;
        return new StandaloneEditorConstructionOptions
        {
            AutomaticLayout = true,
            Language = view.Language,
            Value = view.Value,
            Theme = _prefs.Value.MonacoTheme,
            ReadOnly = view.ReadOnly,
            DomReadOnly = view.ReadOnly,
            FontSize = 13,
            LineHeight = 20,
            FontFamily = "'JetBrains Mono', Consolas, monospace",
            FontLigatures = true,
            Minimap = new EditorMinimapOptions { Enabled = false },
            ScrollBeyondLastLine = false,
            SmoothScrolling = true,
            TabSize = 4,
            WordWrap = view.WordWrap ? "on" : "off",
            GlyphMargin = false,
            Contextmenu = true,
            RenderLineHighlight = view.ReadOnly ? "none" : "line",
            Scrollbar = new EditorScrollbarOptions
            {
                VerticalScrollbarSize = 10,
                HorizontalScrollbarSize = 10
            }
        };
    }

    public Task OnParametersSetAsync()
    {
        if (_disposed || !_ready || _view.Editor is null)
        {
            return Task.CompletedTask;
        }

        _applyQueue = ApplyParametersAsync(_applyQueue);
        return _applyQueue;
    }

    public Task OnInitAsync()
    {
        _init = InitializeEditorAsync();
        return _init;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _prefs.StateChanged -= OnPreferencesChanged;
        _snapshots.Unregister(FlushAsync);
        try
        {
            await _init;
        }
        catch (JSException)
        {
            // JS runtime may already be gone during dispose.
        }

        try
        {
            await _applyQueue;
        }
        catch (JSException)
        {
            // JS runtime may already be gone during dispose.
        }

        await DisposeEditorChromeAsync();
        await _language.DetachEditorAsync(_editorId);
    }

    public Task OnModelChangedAsync(ModelContentChangedEvent args)
    {
        if (_disposed || _suppressChange || !_ready || _view.Editor is null || _view.ReadOnly)
        {
            return Task.CompletedTask;
        }

        return SyncFromEditorAsync(args);
    }

    public Task OnFocusedAsync()
        => _view.OnFocused.HasDelegate ? _view.OnFocused.InvokeAsync() : Task.CompletedTask;

    public Task OnBlurredAsync()
        => _view.ReadOnly ? Task.CompletedTask : _persist.PersistUrlAsync(snapshot: true);

    public Task OnCursorChangedAsync(CursorPositionChangedEvent args)
    {
        var line = args.Position.LineNumber;
        var column = args.Position.Column;
        if (line == _lastCursorLine && column == _lastCursorColumn)
        {
            return Task.CompletedTask;
        }

        _lastCursorLine = line;
        _lastCursorColumn = column;
        return _view.OnCursorChanged.InvokeAsync(args);
    }

    public Task OnKeyDownAsync(KeyboardEvent keyboardEvent)
    {
        if (keyboardEvent.CtrlKey && string.Equals(keyboardEvent.KeyCode.ToString(), "KeyS", StringComparison.OrdinalIgnoreCase)
            || keyboardEvent.MetaKey && string.Equals(keyboardEvent.Code, "KeyS", StringComparison.OrdinalIgnoreCase)
            || keyboardEvent.CtrlKey && string.Equals(keyboardEvent.Code, "KeyS", StringComparison.OrdinalIgnoreCase))
        {
            _dispatcher.Dispatch(new CompileRequestedAction());
        }

        return Task.CompletedTask;
    }

    private async Task ApplyParametersAsync(Task previous)
    {
        try
        {
            await previous;
        }
        catch (JSException)
        {
            // Prior apply already logged; keep the queue alive.
        }

        var editor = _view.Editor;
        if (_disposed || !_ready || editor is null)
        {
            return;
        }

        try
        {
            var modelUriChanged = !string.Equals(_lastModelUri, _view.ModelUri, StringComparison.Ordinal);
            if (modelUriChanged)
            {
                await AttachNamedModelAsync();
                _lastModelUri = _view.ModelUri;
            }

            // SetValue is async. A cached recompile can push Compiling… then the real text
            // before the first SetValue finishes, which left Monaco stuck until a tab switch.
            var valueChanged = false;
            while (!_disposed && !string.Equals(_lastValue, _view.Value, StringComparison.Ordinal))
            {
                var toSet = _view.Value;
                _suppressChange = true;
                try
                {
                    await editor.SetValue(toSet);
                    _lastValue = toSet;
                    valueChanged = true;
                }
                finally
                {
                    _suppressChange = false;
                }
            }

            var languageChanged = !string.Equals(_lastLanguage, _view.Language, StringComparison.Ordinal);
            if (languageChanged)
            {
                var model = await editor.GetModel();
                if (model is not null)
                {
                    await Global.SetModelLanguage(_js, model, _view.Language);
                }

                _lastLanguage = _view.Language;
            }

            if (_lastReadOnly != _view.ReadOnly || _lastWordWrap != _view.WordWrap)
            {
                await editor.UpdateOptions(new EditorUpdateOptions
                {
                    ReadOnly = _view.ReadOnly,
                    WordWrap = _view.WordWrap ? "on" : "off"
                });
                _lastReadOnly = _view.ReadOnly;
                _lastWordWrap = _view.WordWrap;
            }

            if (!_disposed && (modelUriChanged || valueChanged || languageChanged))
            {
                await _language.OnEditorReadyAsync(_editorId, _view.ModelUri, _view.ReadOnly, fold: languageChanged);
            }
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Applying editor parameters failed.");
            _ready = false;
            _suppressChange = false;
        }
    }

    private async Task InitializeEditorAsync()
    {
        if (_disposed)
        {
            return;
        }

        _lastValue = _view.Value;
        _lastLanguage = _view.Language;
        _lastModelUri = _view.ModelUri;
        _ready = true;
        await AttachNamedModelAsync();
        if (_disposed)
        {
            return;
        }

        await ApplyMonacoThemeAsync();
        await ApplyVirtualKeyboardAsync();
        await ApplyVimAsync();
        await ApplyWrapAndReadOnlyAsync();
        if (_disposed)
        {
            return;
        }

        await _language.InitializeAsync();
        if (_disposed)
        {
            return;
        }

        await _language.OnEditorReadyAsync(_editorId, _view.ModelUri, _view.ReadOnly, fold: true);
    }

    private async Task ApplyWrapAndReadOnlyAsync()
    {
        var editor = _view.Editor;
        if (editor is null)
        {
            return;
        }

        try
        {
            await editor.UpdateOptions(new EditorUpdateOptions
            {
                ReadOnly = _view.ReadOnly,
                WordWrap = _view.WordWrap ? "on" : "off"
            });
            _lastReadOnly = _view.ReadOnly;
            _lastWordWrap = _view.WordWrap;
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Applying wrap and read-only options failed.");
        }
    }

    private async Task AttachNamedModelAsync()
    {
        var editor = _view.Editor;
        var modelUri = _view.ModelUri;
        if (editor is null || string.IsNullOrEmpty(modelUri))
        {
            return;
        }

        try
        {
            _suppressChange = true;
            var existing = await Global.GetModel(_js, modelUri);
            TextModel model;
            if (existing is null)
            {
                model = await Global.CreateModel(_js, _view.Value, _view.Language, modelUri);
            }
            else
            {
                model = existing;
                var text = await model.GetValue(EndOfLinePreference.TextDefined, preserveBOM: true);
                if (!string.Equals(text, _view.Value, StringComparison.Ordinal))
                {
                    await model.SetValue(_view.Value);
                }
            }

            await editor.SetModel(model);
            _lastValue = _view.Value;
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Attaching the named editor model failed.");
        }
        finally
        {
            _suppressChange = false;
        }
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_ready)
        {
            return;
        }

        if (!string.Equals(_appliedMonacoTheme, _prefs.Value.MonacoTheme, StringComparison.Ordinal))
        {
            _ = ApplyMonacoThemeAsync();
        }

        _ = ApplyVirtualKeyboardAsync();
        _ = ApplyVimAsync();
    }

    private async Task ApplyVirtualKeyboardAsync()
    {
        var disabled = _view.ReadOnly || _prefs.Value.DisableInputVirtualKeyboard;
        if (_appliedKeyboardDisabled == disabled)
        {
            return;
        }

        if (_appliedKeyboardDisabled is null && !disabled)
        {
            _appliedKeyboardDisabled = false;
            return;
        }

        try
        {
            await _js.InvokeVoidAsync("netLabKeyboard.setDisabled", _editorId, disabled);
            _appliedKeyboardDisabled = disabled;
        }
        catch (JSException ex)
        {
            _logger.LogDebug(ex, "Applying virtual keyboard state failed.");
        }
    }

    private async Task ApplyVimAsync()
    {
        var enabled = !_view.ReadOnly && _prefs.Value.UseVim;
        if (_appliedVim == enabled)
        {
            return;
        }

        if (_appliedVim is null && !enabled)
        {
            _appliedVim = false;
            return;
        }

        try
        {
            if (enabled)
            {
                await _js.InvokeVoidAsync("netLabVim.enable", _editorId, VimStatusId);
            }
            else
            {
                await _js.InvokeVoidAsync("netLabVim.dispose", _editorId);
            }

            _appliedVim = enabled;
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Applying vim mode failed.");
        }
    }

    private async Task ApplyMonacoThemeAsync()
    {
        try
        {
            _appliedMonacoTheme = _prefs.Value.MonacoTheme;
            await _js.InvokeVoidAsync("netLabMonaco.applyTheme", _prefs.Value.ResolvedDark);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Applying the Monaco theme failed.");
        }
    }

    private async Task SyncFromEditorAsync(ModelContentChangedEvent args)
    {
        var editor = _view.Editor;
        if (editor is null)
        {
            return;
        }

        try
        {
            var next = await editor.GetValue();
            PushSource(next);
            if (!string.IsNullOrEmpty(_view.ModelUri))
            {
                await _language.OnSourceModelContentChangedAsync(_view.ModelUri, args);
            }
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Reading the editor value after a change failed.");
        }
    }

    private void PushSource(string next)
    {
        _lastValue = next;
        if (!string.IsNullOrEmpty(_view.FileName))
        {
            _documents.SetSource(_view.FileName, next);
        }
        else if (_view.OnValueChanged.HasDelegate)
        {
            _ = _view.OnValueChanged.InvokeAsync(next);
        }
    }

    private async Task FlushAsync()
    {
        var editor = _view.Editor;
        if (_disposed || _view.ReadOnly || !_ready || editor is null)
        {
            return;
        }

        try
        {
            var next = await editor.GetValue();
            if (string.Equals(_lastValue, next, StringComparison.Ordinal))
            {
                return;
            }

            PushSource(next);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Flushing the editor value failed.");
        }
    }

    private async Task DisposeEditorChromeAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("netLabVim.dispose", _editorId);
        }
        catch (JSException)
        {
            // JS runtime may already be gone during dispose.
        }

        try
        {
            await _js.InvokeVoidAsync("netLabKeyboard.dispose", _editorId);
        }
        catch (JSException)
        {
            // JS runtime may already be gone during dispose.
        }
    }
}
