// ============================================================================
// MauiProgram.cs — the MAUI bootstrap (equivalent of a Main method).
// Each platform head (Platforms/Windows, Platforms/MacCatalyst) calls
// CreateMauiApp() to spin up the shared cross-platform application.
// ============================================================================

using Microsoft.Maui.Hosting;

namespace EslEpubReader;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        return builder.Build();
    }
}
