// Mac Catalyst app delegate: hands control to the shared MauiProgram.
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace EslEpubReader;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
