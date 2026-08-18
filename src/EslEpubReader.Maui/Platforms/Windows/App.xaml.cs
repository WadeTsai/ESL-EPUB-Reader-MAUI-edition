// Windows platform head code-behind: hands control to the shared MauiProgram.
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace EslEpubReader.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
