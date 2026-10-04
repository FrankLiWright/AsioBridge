using System.Runtime.InteropServices;

namespace AsioBridge;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        // Optional headless start: AsioBridge.exe --start --driver "Name" --pid 1234 --buffer 50
        if (args.Length > 0 && Array.Exists(args, a => a is "--list" or "-l"))
        {
            RunList();
            return;
        }
        if (args.Length > 0 && Array.Exists(args, a => a is "--start" or "-s"))
        {
            RunHeadless(args);
            return;
        }

        Application.Run(new MainForm());
    }

    private static void RunList()
    {
        Console.WriteLine("ASIO drivers:");
        foreach (var d in Audio.BridgeEngine.GetAsioDriverNames())
            Console.WriteLine("  " + d);

        Console.WriteLine("Render audio sessions:");
        foreach (var s in Audio.AudioSessionList.GetDefaultRenderSessions())
            Console.WriteLine($"  pid={s.ProcessId}  {s.ProcessName}  ({s.DisplayName})");
    }

    private static void RunHeadless(string[] args)
    {
        string? driver = GetArg(args, "--driver") ?? GetArg(args, "-d");
        string? mode = GetArg(args, "--mode") ?? "process";
        string? pidStr = GetArg(args, "--pid") ?? GetArg(args, "-p");
        string? bufferStr = GetArg(args, "--buffer");
        string? children = GetArg(args, "--children");

        var engine = new Audio.BridgeEngine();
        engine.Error += msg => Console.Error.WriteLine("[error] " + msg);
        engine.StatusChanged += s =>
        {
            Console.WriteLine($"running={s.IsRunning} buf={s.BufferedMs}ms underruns={s.Underruns} peakL={s.PeakLeft:F2} peakR={s.PeakRight:F2}");
        };

        var options = new Audio.BridgeOptions
        {
            AsioDriverName = driver,
            Mode = string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase)
                ? Audio.CaptureMode.SystemLoopback
                : Audio.CaptureMode.ProcessLoopback,
            TargetProcessId = int.TryParse(pidStr, out var pid) ? pid : 0,
            IncludeChildProcesses = !string.Equals(children, "false", StringComparison.OrdinalIgnoreCase),
            BufferMs = int.TryParse(bufferStr, out var b) ? b : 50,
        };

        try
        {
            engine.Start(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("启动失败: " + ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine("AsioBridge running. Press Ctrl+C to stop.");
        var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };
        quit.Wait();
        engine.Stop();
        engine.Dispose();
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
