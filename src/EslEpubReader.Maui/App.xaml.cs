// ============================================================================
// App.xaml.cs — creates the single main window hosting MainPage.
// ============================================================================

namespace EslEpubReader;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    /// <summary>One window, one page — the reader. A comfortable default
    /// size on desktop; the OS remembers user resizing per its own rules.</summary>
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage())
        {
            Title = "ESL EPUB Reader",
            Width = 1500,
            Height = 900,
        };
}
