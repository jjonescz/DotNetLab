using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

using AndroidView = Android.Views.View;

namespace DotNetLab;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize |
        ConfigChanges.Orientation |
        ConfigChanges.UiMode |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        WindowCompat.SetDecorFitsSystemWindows(Window, false);

        if (FindViewById(Android.Resource.Id.Content) is { } contentView)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(
                contentView,
#pragma warning disable CA2000 // Dispose objects before losing scope
                new SystemBarsInsetsListener(contentView));
#pragma warning restore CA2000
            ViewCompat.RequestApplyInsets(contentView);
        }
    }

    private sealed class SystemBarsInsetsListener(AndroidView view) : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        private readonly int initialPaddingLeft = view.PaddingLeft;
        private readonly int initialPaddingTop = view.PaddingTop;
        private readonly int initialPaddingRight = view.PaddingRight;
        private readonly int initialPaddingBottom = view.PaddingBottom;

        public WindowInsetsCompat? OnApplyWindowInsets(AndroidView? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null)
            {
                return insets;
            }

            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            if (systemBars is null)
            {
                return insets;
            }

            v.SetPadding(
                initialPaddingLeft + systemBars.Left,
                initialPaddingTop + systemBars.Top,
                initialPaddingRight + systemBars.Right,
                initialPaddingBottom + systemBars.Bottom);

            return insets;
        }
    }
}
