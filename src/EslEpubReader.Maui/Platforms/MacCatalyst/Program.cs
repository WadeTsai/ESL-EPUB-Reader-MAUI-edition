// Mac Catalyst platform head: standard UIKit entry point. This file only
// compiles in the net10.0-maccatalyst target (built on a Mac).
using ObjCRuntime;
using UIKit;

namespace EslEpubReader;

public class Program
{
    static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
