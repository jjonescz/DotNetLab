namespace DotNetLab;

public sealed class AndroidMauiApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage());
    }
}
