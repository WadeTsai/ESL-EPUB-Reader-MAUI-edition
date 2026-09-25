// ============================================================================
// GLibSynchronizationContext.cs — posts continuations to the GLib main loop.
//
// GTK widgets may only be touched from the main thread. Installing this
// context before the first await means every `await` in MainWindow resumes
// on the GTK thread (the lookup services' HttpClient calls complete on the
// thread pool), exactly like MAUI's dispatcher does for MainPage.
// ============================================================================

namespace EslEpubReader;

public sealed class GLibSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state)
    {
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
        {
            try { d(state); }
            catch (Exception ex)
            {
                // An exception escaping into native GLib would abort the
                // process; report it and keep the reader alive instead.
                Console.Error.WriteLine($"Unhandled exception on the GTK thread: {ex}");
            }
            return false;   // one-shot source
        });
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Current == this) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try { d(state); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }, null);
        done.Wait();
        if (error is not null) throw new AggregateException(error);
    }

    public override SynchronizationContext CreateCopy() => this;
}
