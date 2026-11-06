using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using static ProbeHost.Native;

namespace ProbeHost;

public sealed class TestTarget
{
    public string Name;
    public IntPtr Hwnd;
    public Process Process;
    public Window Window;
    public string Kind;          // notepad / paint / wpf / wpf-layered
    public RECT InitialRect;
}

public sealed class ProbeResult
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Status { get; set; }       // PASS / FAIL / N/A
    public string Summary { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public static partial class Program
{
    private static readonly List<ProbeResult> Results = new();
    private static readonly List<TestTarget> Targets = new();
    private static readonly StringBuilder LogBuf = new();
    private static Application _app;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fg-child") return ForegroundChild(args);
        if (args.Length > 0 && args[0] == "--dpi-child") return DpiChild(args);
        if (args.Length > 0 && args[0] == "--claim-hotkey") return ClaimHotkeyChild();

        if (args.Length > 0 && args[0] == "--readonly")
        {
            ReadOnlyProbe.Run();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--interactive")
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            _app.Startup += (_, _) => { _ = RunAll(); };
            _app.Run();
            return 0;
        }

        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Dock 探针（默认不运行任何探针，防止误操作）");
        Console.WriteLine();
        Console.WriteLine("  ProbeHost.exe --readonly     只读：枚举窗口/矩形差/显示器 DPI/权限边界，不创建、不移动任何窗口");
        Console.WriteLine("  ProbeHost.exe --interactive  交互探针：会新建测试窗口并移动它们（请先关闭手头工作并自行确认）");
        Console.WriteLine();
        Console.WriteLine("交互探针包含：前台激活(4 种做法 × 20 次)、覆盖层框选、原位还原、拖入、全局热键、跨 DPI 还原。");
        Console.WriteLine("详见 README.md。辅助参数 --fg-child / --dpi-child / --claim-hotkey 由主程序内部调用，请勿手动使用。");
    }

    private static async Task RunAll()
    {
        Directory.CreateDirectory("out");
        Log($"PROBE START {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Log($"OS={Environment.OSVersion.Version} x64={Environment.Is64BitProcess} awareness=PerMonitorV2");
        try
        {
            await SetupTargets();
            await ProbeForeground();
            await ProbeRects();
            await ProbeDpi();
            await ProbeEnumeration();
            await ProbeRestore();
            await ProbeDrop();
            await ProbeOverlay();
            await ProbeHotkey();
        }
        catch (Exception ex)
        {
            Log("FATAL " + ex);
            Results.Add(new ProbeResult { Id = "FATAL", Name = "runner", Status = "FAIL", Summary = ex.Message });
        }
        finally
        {
            CloseTargets();
        }

        Log("");
        Log("ID\tSTATUS\tSUMMARY");
        foreach (var r in Results) Log($"{r.Id}\t{r.Status}\t{r.Summary}");
        var json = JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("out/probe-results.json", json);
        File.WriteAllText("out/probe-console.txt", LogBuf.ToString());
        Log("WROTE out/probe-results.json");
        _app.Shutdown();
    }

    public static void Log(string s)
    {
        Console.WriteLine(s);
        LogBuf.AppendLine(s);
    }

    private static async Task SetupTargets()
    {
        // 所有靶窗口都是本探针自己创建的，绝不触碰用户已有窗口。
        Targets.Add(await LaunchApp("notepad.exe", "notepad-1", "notepad"));
        Targets.Add(await LaunchApp("notepad.exe", "notepad-2", "notepad"));
        Targets.Add(await LaunchApp("mspaint.exe", "mspaint", "paint"));
        Targets.Add(CreateWpfTarget("wpf-normal", layered: false));
        Targets.Add(CreateWpfTarget("wpf-layered", layered: true));

        var mon = Native.Monitors()[0];
        int w = 520, h = 300, gap = 30;
        int right = mon.info.rcWork.Right - 40;
        int x1 = right - 2 * w - gap, x2 = right - w;
        int y1 = mon.info.rcWork.Top + 90;
        var spots = new[] { (x1, y1), (x2, y1), (x1, y1 + h + gap), (x2, y1 + h + gap), (x1, y1 + 2 * (h + gap)) };
        for (int i = 0; i < Targets.Count; i++)
        {
            var t = Targets[i];
            if (t.Hwnd == IntPtr.Zero) continue;
            Native.ShowWindow(t.Hwnd, Native.SW_RESTORE);
            Place(t.Hwnd, spots[i].Item1, spots[i].Item2, w, h);
            await Task.Delay(300);
            Native.GetWindowRect(t.Hwnd, out t.InitialRect);
        }
        await Task.Delay(500);
        Log("TARGETS " + string.Join(", ", Targets.Where(t => t.Hwnd != IntPtr.Zero)
            .Select(t => $"{t.Name}={t.Hwnd.ToInt64():X}")));
    }

    private static async Task<TestTarget> LaunchApp(string exe, string name, string kind)
    {
        var t = new TestTarget { Name = name, Kind = kind };
        try
        {
            t.Process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            t.Hwnd = await WaitWindow(t.Process.Id, 8000);
            Log($"LAUNCH {name} pid={t.Process?.Id} hwnd=0x{t.Hwnd.ToInt64():X}");
        }
        catch (Exception ex) { Log($"LAUNCH-FAIL {name}: {ex.Message}"); }
        return t;
    }

    private static TestTarget CreateWpfTarget(string name, bool layered)
    {
        var t = new TestTarget { Name = name, Kind = layered ? "wpf-layered" : "wpf" };
        try
        {
            var w = new Window
            {
                Title = name,
                Width = 520,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 50,
                Top = 50,
                ShowInTaskbar = true,
                Background = new SolidColorBrush(Color.FromRgb(32, 90, 160)),
                Content = new TextBlock
                {
                    Text = name,
                    Foreground = Brushes.White,
                    FontSize = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            if (layered)
            {
                w.WindowStyle = WindowStyle.None;
                w.AllowsTransparency = true;
                w.Background = Brushes.Transparent;
                w.Content = new Border
                {
                    Margin = new Thickness(24),
                    CornerRadius = new CornerRadius(18),
                    Background = new SolidColorBrush(Color.FromRgb(180, 40, 40)),
                    Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.7 },
                    Child = new TextBlock
                    {
                        Text = name,
                        Foreground = Brushes.White,
                        FontSize = 24,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
            }
            w.Show();
            t.Window = w;
            t.Hwnd = new WindowInteropHelper(w).Handle;
            Log($"LAUNCH {name} hwnd=0x{t.Hwnd.ToInt64():X}");
        }
        catch (Exception ex) { Log($"LAUNCH-FAIL {name}: {ex.Message}"); }
        return t;
    }

    private static async Task<IntPtr> WaitWindow(int pid, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var found = FindWindowByPid(pid);
            if (found != IntPtr.Zero) return found;
            await Task.Delay(150);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindWindowByPid(int pid)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid && Native.IsWindowVisible(h) && Native.GetWindowTextLengthW(h) > 0)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Place(IntPtr h, int x, int y, int w, int ht)
        => Native.SetWindowPos(h, IntPtr.Zero, x, y, w, ht,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    public static void Offset(IntPtr h, int dx, int dy, int dw, int dh)
    {
        Native.GetWindowRect(h, out RECT r);
        Native.SetWindowPos(h, IntPtr.Zero, r.Left + dx, r.Top + dy,
            Math.Max(80, r.Right - r.Left + dw), Math.Max(60, r.Bottom - r.Top + dh),
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public static bool ActivateAttach(IntPtr target)
    {
        IntPtr fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        bool attached = fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try
        {
            Native.ShowWindow(target, Native.SW_RESTORE);
            bool ok = Native.SetForegroundWindow(target);
            return ok;
        }
        finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
    }

    private static void CloseTargets()
    {
        foreach (var t in Targets)
        {
            try
            {
                if (t.Window != null) t.Window.Close();
                else if (t.Process != null && !t.Process.HasExited)
                {
                    t.Process.CloseMainWindow();
                    if (!t.Process.WaitForExit(3000)) t.Process.Kill();
                }
            }
            catch { }
        }
    }

    private static ProbeResult Add(ProbeResult r)
    {
        Results.Add(r);
        Log($"[{r.Status}] {r.Id}: {r.Summary}");
        return r;
    }

    private static ProbeResult R(string id, string name, string status, string summary)
        => new ProbeResult { Id = id, Name = name, Status = status, Summary = summary };
}
