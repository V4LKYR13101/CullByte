using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace CullByte;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime lifetime)
            lifetime.MainViewFactory = () =>
            {
                var activity = MainActivity.Current ?? throw new InvalidOperationException("Android activity is unavailable.");
                return new MobileView(activity.Storage);
            };
        base.OnFrameworkInitializationCompleted();
    }
}
