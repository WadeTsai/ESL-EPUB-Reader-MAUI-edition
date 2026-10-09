// ============================================================================
// Program.cs — the Linux entry point (GTK 4 + WebKitGTK via GirCore).
//
// One GtkApplication, one MainWindow. The application id makes GTK run a
// SINGLE instance: launching again (e.g. double-clicking another .epub)
// hands the file to the running window instead of opening a second one.
// ============================================================================

// GTK/WebKitGTK only exist here; tells the platform analyzer so.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace EslEpubReader;

public static class Program
{
    public const string AppId = "com.wadetsai.eslepubreader";

    public static int Main(string[] args)
    {
        WebKit.Module.Initialize();

        var app = Gtk.Application.New(AppId, Gio.ApplicationFlags.HandlesOpen);
        MainWindow? window = null;

        MainWindow EnsureWindow()
        {
            if (window is null)
            {
                // Route every `await` continuation back onto the GTK main
                // loop — the same guarantee MAUI's dispatcher gives MainPage.
                SynchronizationContext.SetSynchronizationContext(new GLibSynchronizationContext());
                window = new MainWindow(app);
            }
            return window;
        }

        // Plain launch: show the window (it reopens the last book itself).
        app.OnActivate += (_, _) => EnsureWindow().Present();

        // Launched with a file (`eslepubreader book.epub`, or the desktop
        // entry's %f): open that book instead of the remembered one.
        app.OnOpen += (_, e) =>
        {
            MainWindow w = EnsureWindow();
            w.Present();
            string? path = e.Files.Length > 0 ? e.Files[0].GetPath() : null;
            if (path is not null) w.OpenFromCommandLine(path);
        };

        // GApplication expects a C-style argv whose [0] is the program name;
        // .NET's args omit it (without this, a file argument would be taken
        // as the program name and never opened).
        return app.RunWithSynchronizationContext(["eslepubreader", .. args]);
    }
}
