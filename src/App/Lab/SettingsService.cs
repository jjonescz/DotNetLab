using System.ComponentModel;
using PolyType;

namespace DotNetLab.Lab;

[GenerateShape]
internal sealed partial class SettingsService : INotifyPropertyChanged
{
    private readonly LocalStorageService localStorage;
    private readonly Lazy<Task> loadTask;
    private readonly SerializedPropertyList changedProperties = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public SettingsService(LocalStorageService localStorage, IAppHostEnvironment hostEnvironment)
    {
        this.localStorage = localStorage;
        loadTask = new(ReloadAsync);

        // Set default values.
        DebugLogs = hostEnvironment.IsDevelopment;
        EnableLanguageServices = true;
        EnableCaching = true;
        AutoCompileOnStart = true;
        CompilationPreferences = CompilationPreferences.Default;

        // After default values are set, subscribe to property changes.
        PropertyChanged += static (sender, e) =>
        {
            var @this = (SettingsService)sender!;
            LocalStorageService.SerializeProperty(@this, e.PropertyName!, @this.changedProperties);
        };
    }

    public Task LoadIfNeededAsync() => loadTask.Value;

    public Task ReloadAsync()
    {
        return localStorage.TryLoadPropertiesAsync(this);
    }

    public Task SaveAsync()
    {
        return changedProperties.IsEmpty
            ? Task.CompletedTask
            : localStorage.SavePropertiesAsync(changedProperties);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [OnPropertyChanged] public partial bool WordWrap { get; set; }

    [OnPropertyChanged] public partial bool UseVim { get; set; }

    [OnPropertyChanged] public partial bool DebugLogs { get; set; }

    [OnPropertyChanged] public partial bool TraceLogs { get; set; }

    [OnPropertyChanged] public partial bool EnableMemoryUsageView { get; set; }

    [DisplayName("EnableLanguageServices2")] // Turning this on by default for existing users means we need new key, hence the `2`.
    [OnPropertyChanged] public partial bool EnableLanguageServices { get; set; }

    [OnPropertyChanged] public partial bool EnableCaching { get; set; }

    [OnPropertyChanged] public partial bool AutoCompileOnStart { get; set; }

    [DisplayName("displayHintSquiggles")] // Legacy naming, kept so values set in older versions continue to be loaded.
    [OnPropertyChanged] public partial bool DisplayHintSquiggles { get; set; }

    [DisplayName("disableInputVirtualKeyboard")] // Legacy naming, kept so values set in older versions continue to be loaded.
    [OnPropertyChanged] public partial bool DisableInputVirtualKeyboard { get; set; }

    [OnPropertyChanged] public partial CompilationPreferences CompilationPreferences { get; set; }
}
