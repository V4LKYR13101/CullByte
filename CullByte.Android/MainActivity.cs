using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia.Android;
using AndroidUri = Android.Net.Uri;

namespace CullByte;

[Application]
public sealed class AndroidApp(nint handle, JniHandleOwnership transfer) :
    AvaloniaAndroidApplication<App>(handle, transfer);

[Activity(Label = "CullByte", MainLauncher = true, Exported = true,
    Icon = "@drawable/cullbyte_icon", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.KeyboardHidden | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity
{
    internal static MainActivity? Current { get; private set; }
    internal AndroidStorage Storage { get; private set; } = null!;
    private TaskCompletionSource<AndroidUri?>? picker;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        Storage = new AndroidStorage(this);
        base.OnCreate(savedInstanceState);
    }

    protected override void OnDestroy()
    {
        picker?.TrySetResult(null);
        if (Current == this) Current = null;
        base.OnDestroy();
    }

    internal async Task<AndroidUri?> PickAsync(Intent intent)
    {
        if (picker is not null) return null;
        var request = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker = request;
        try
        {
#pragma warning disable CS0618 // The picker result is routed through Avalonia's Activity.
            StartActivityForResult(intent, 801);
#pragma warning restore CS0618
            return await request.Task;
        }
        finally
        {
            if (picker == request) picker = null;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == 801) picker?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }
}

