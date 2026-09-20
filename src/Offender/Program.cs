using Offender.Native;
using Offender.Sampling;
using Offender.Ui;

namespace Offender;

internal static unsafe class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    private static int Main(string[] args)
    {
        // Creating the Start Menu shortcut is also useful from a script or an installer,
        // and having it as an entry point makes the COM path testable on its own.
        if (args.Length > 0 && args[0] is "--make-shortcut" or "/make-shortcut")
        {
            bool ok = Startup.CreateStartMenuShortcut();
            Console.WriteLine(ok ? "shortcut created" : "shortcut FAILED");
            return ok ? 0 : 2;
        }

        // A second copy would just duplicate the tray icon and double the sampling cost.
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Offender.SingleInstance", out bool isFirst);
        if (!isFirst) return 0;

        var config = Config.Load();

        // The registry is the source of truth for the startup toggle; the config file only
        // remembers what the user last chose. Reconcile them at launch.
        config.StartWithWindows = Startup.IsEnabled();

        using var sampler = new Sampler
        {
            IntervalMs = config.IntervalMs,
            ProcessEveryNTicks = Math.Max(1, 2000 / Math.Max(250, config.IntervalMs)),
            PinnedInterface = string.IsNullOrWhiteSpace(config.PinnedInterface) ? null : config.PinnedInterface,
            GpuWanted = config.ShowGpu || config.NotchGpu,
        };

        using var window = new PanelWindow(config, sampler);
        if (!window.Create()) return 1;

        sampler.OnSnapshot = window.NotifySnapshot;
        sampler.Start();

        // Startup touches far more memory than steady state does. Handing the working set
        // back now means the number a user sees in Task Manager reflects the real cost.
        Win32.SetProcessWorkingSetSize(Win32.GetCurrentProcess(), -1, -1);

        window.RunMessageLoop();

        config.Save();
        return 0;
    }
}
