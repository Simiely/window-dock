using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static ProbeHost.Native;

namespace ProbeHost;

public static partial class Program
{
    // ---------------- 1. 前台激活 --------------
    private static async Task ProbeForeground()
    {
        var a = Targets.FirstOrDefault(t => t.Name == "notepad-1" && t.Hwnd != IntPtr.Zero);
        var b = Targets.FirstOrDefault(t => t.Name == "notepad-2" && t.Hwnd != IntPtr.Zero);
        if (a == null || b == null)
        {
            Add(R("P1", "前台激活", "N/A", "未能创建两个记事本靶窗口"));
            return;
        }

        var variants = new[] { "direct", "allow", "attach", "alt" };
        var stats = new Dictionary<string, (int ok, int apiTrue, int baselineFail, int n)>();

        foreach (var v in variants)
        {
            int ok = 0, apiTrue = 0, baseFail = 0, n = 0;
            for (int i = 0; i < 20; i++)
            {
                IntPtr target = (i % 2 == 0) ? a.Hwnd : b.Hwnd;
                IntPtr other = (target == a.Hwnd) ? b.Hwnd : a.Hwnd;
                bool baseline = false;
                for (int k = 0; k < 3 && !baseline; k++)
                {
                    ActivateAttach(other);
                    await Task.Delay(150);
                    baseline = Native.GetForegroundWindow() == other;
                }
                if (!baseline) { baseFail++; continue; }
                n++;
                var child = await RunForegroundChild(target, v);
                if (child.ok) ok++;
                if (child.apiTrue) apiTrue++;
                await Task.Delay(120);
            }
            stats[v] = (ok, apiTrue, baseFail, n);
            Log($"FG {v}: ok={ok}/{n} apiTrue={apiTrue} baselineFail={baseFail}");
        }

        // 提权窗口：不触发 UAC，只统计当前桌面上存在多少提权窗口，作为后续人工验证的前提。
        int elevated = 0;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != (uint)Environment.ProcessId && Native.IsElevatedPid(pid)) elevated++;
            return true;
        }, IntPtr.Zero);

        var r = R("P1", "前台激活", "PASS",
            $"direct {stats["direct"].ok}/20, allow {stats["allow"].ok}/20, attach {stats["attach"].ok}/20, alt {stats["alt"].ok}/20");
        foreach (var kv in stats)
        {
            r.Data[kv.Key + "_ok"] = kv.Value.ok;
            r.Data[kv.Key + "_trials"] = kv.Value.n;
            r.Data[kv.Key + "_api_true"] = kv.Value.apiTrue;
            r.Data[kv.Key + "_baseline_fail"] = kv.Value.baselineFail;
        }
        r.Data["elevated_windows_seen"] = elevated;
        r.Notes.Add("每次试验由新启动的子进程执行，避免父进程已获得前台权限造成假阳性。");
        r.Notes.Add("成功判据：调用后 250ms 内 GetForegroundWindow()==目标。只闪任务栏在无人值守环境下表现为返回 FALSE 且前台不变。");
        r.Notes.Add($"提权靶窗口需 UAC 交互，本机未构造；当前桌面检测到 {elevated} 个提权进程窗口（仅读取，未触碰）。");
        Add(r);
    }

    private static async Task<(bool ok, bool apiTrue)> RunForegroundChild(IntPtr target, string variant)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--fg-child");
        psi.ArgumentList.Add(target.ToInt64().ToString());
        psi.ArgumentList.Add(variant);
        using var p = Process.Start(psi);
        string line = await p.StandardOutput.ReadLineAsync();
        p.WaitForExit(5000);
        // FGCHILD|variant|api=True|before=..|after=..|target=..
        var parts = (line ?? "").Split('|');
        if (parts.Length < 6) return (false, false);
        bool api = parts[2].EndsWith("True", StringComparison.OrdinalIgnoreCase);
        bool ok = parts[4].EndsWith(target.ToInt64().ToString());
        return (ok, api);
    }

    private static int ForegroundChild(string[] args)
    {
        IntPtr target = new IntPtr(long.Parse(args[1]));
        string variant = args[2];
        IntPtr before = Native.GetForegroundWindow();
        bool api = false;
        switch (variant)
        {
            case "direct":
                api = Native.SetForegroundWindow(target);
                break;
            case "allow":
                Native.GetWindowThreadProcessId(target, out uint pid);
                Native.AllowSetForegroundWindow(pid);
                api = Native.SetForegroundWindow(target);
                break;
            case "attach":
                api = ActivateAttach(target);
                break;
            case "alt":
                Native.keybd_event(Native.VK_MENU, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                Native.keybd_event(Native.VK_MENU, 0, 2, UIntPtr.Zero);
                Thread.Sleep(40);
                api = Native.SetForegroundWindow(target);
                break;
        }
        Thread.Sleep(250);
        IntPtr after = Native.GetForegroundWindow();
        Console.WriteLine($"FGCHILD|{variant}|api={api}|before={before.ToInt64()}|after={after.ToInt64()}|target={target.ToInt64()}");
        return 0;
    }

    // ---------------- 2. 命中判定 --------------
    private static async Task ProbeRects()
    {
        var r = R("P2", "命中判定", "PASS", "");
        var samples = new List<string>();
        foreach (var t in Targets.Where(x => x.Hwnd != IntPtr.Zero))
        {
            Native.GetWindowRect(t.Hwnd, out RECT wr);
            RECT vr = Native.VisualRect(t.Hwnd);
            var d = new
            {
                name = t.Name,
                window = $"{wr.Left},{wr.Top},{wr.Right},{wr.Bottom}",
                visual = $"{vr.Left},{vr.Top},{vr.Right},{vr.Bottom}",
                dl = wr.Left - vr.Left,
                dt = wr.Top - vr.Top,
                dr = wr.Right - vr.Right,
                db = wr.Bottom - vr.Bottom,
                w = wr.Right - wr.Left,
                h = wr.Bottom - wr.Top
            };
            r.Data[t.Name] = d;
            samples.Add($"{t.Name}: L{d.dl} T{d.dt} R{d.dr} B{d.db} (win {d.w}x{d.h})");
        }

        var note = Targets.FirstOrDefault(t => t.Name == "notepad-1" && t.Hwnd != IntPtr.Zero);
        if (note != null)
        {
            Native.ShowWindow(note.Hwnd, Native.SW_MAXIMIZE);
            await Task.Delay(400);
            Native.GetWindowRect(note.Hwnd, out RECT mw);
            RECT mv = Native.VisualRect(note.Hwnd);
            r.Data["notepad-1_maximized"] = new
            {
                window = $"{mw.Left},{mw.Top},{mw.Right},{mw.Bottom}",
                visual = $"{mv.Left},{mv.Top},{mv.Right},{mv.Bottom}",
                dl = mw.Left - mv.Left, dt = mw.Top - mv.Top,
                dr = mw.Right - mv.Right, db = mw.Bottom - mv.Bottom
            };
            Native.ShowWindow(note.Hwnd, Native.SW_RESTORE);
            await Task.Delay(300);
            Native.SetWindowPos(note.Hwnd, IntPtr.Zero, note.InitialRect.Left, note.InitialRect.Top,
                note.InitialRect.Right - note.InitialRect.Left, note.InitialRect.Bottom - note.InitialRect.Top,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        r.Summary = string.Join(" | ", samples);
        r.Notes.Add("差值为 GetWindowRect - EXTENDED_FRAME_BOUNDS：正值表示窗口矩形比视觉边界更靠外（阴影）。");
        Add(r);
    }

    // ---------------- 3. 混合 DPI --------------
    private static async Task ProbeDpi()
    {
        var r = R("P3", "混合 DPI", "PASS", "");
        var mons = Native.Monitors();
        var list = new List<object>();
        var probe = Targets.FirstOrDefault(t => t.Name == "wpf-normal" && t.Hwnd != IntPtr.Zero);
        foreach (var m in mons)
        {
            uint dpi = 0;
            if (probe != null)
            {
                int cx = (m.info.rcWork.Left + m.info.rcWork.Right) / 2 - 260;
                int cy = (m.info.rcWork.Top + m.info.rcWork.Bottom) / 2 - 150;
                Native.SetWindowPos(probe.Hwnd, IntPtr.Zero, cx, cy, 520, 300, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                await Task.Delay(500);
                dpi = Native.GetDpiForWindow(probe.Hwnd);
            }
            list.Add(new
            {
                device = m.info.szDevice,
                rect = $"{m.info.rcMonitor.Left},{m.info.rcMonitor.Top},{m.info.rcMonitor.Right},{m.info.rcMonitor.Bottom}",
                work = $"{m.info.rcWork.Left},{m.info.rcWork.Top},{m.info.rcWork.Right},{m.info.rcWork.Bottom}",
                dpi_api = m.dpi,
                dpi_window_probe = dpi,
                scale = Native.ScalePercent(dpi)
            });
        }
        r.Data["monitors"] = list;
        if (probe != null)
        {
            Native.SetWindowPos(probe.Hwnd, IntPtr.Zero, 300, 300, 520, 300, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(300);
        }

        var target = Targets.FirstOrDefault(t => t.Name == "notepad-1" && t.Hwnd != IntPtr.Zero);
        if (target != null)
        {
            Native.GetWindowRect(target.Hwnd, out RECT wr);
            uint dpi = Native.GetDpiForWindow(target.Hwnd);
            var ptPhys = new Native.POINT { X = wr.Left, Y = wr.Top };
            bool p2l = Native.PhysicalToLogicalPointForPerMonitorDPI(target.Hwnd, ref ptPhys);
            int expectedDipX = (int)Math.Round(wr.Left * 96.0 / dpi);
            r.Data["pmv2_target"] = new
            {
                rect = $"{wr.Left},{wr.Top},{wr.Right},{wr.Bottom}",
                dpi,
                scale = Native.ScalePercent(dpi),
                phys_left = wr.Left,
                p2l_ok = p2l,
                logical_left = ptPhys.X,
                expected_dip_rounded = expectedDipX
            };

            var unaware = RunDpiChild(target.Hwnd);
            if (unaware != null)
            {
                var parts = unaware.Split('|');
                r.Data["unaware_child"] = new
                {
                    raw = unaware,
                    rect = parts.Length > 1 ? parts[1] : "",
                    dpi = parts.Length > 2 ? parts[2] : "",
                    sys = parts.Length > 3 ? parts[3] : ""
                };
            }
        }

        r.Summary = $"monitors={mons.Count}, " + string.Join(", ", mons.Select((m, i) =>
            $"{m.info.szDevice}@{Native.ScalePercent(m.dpi)}%"));
        r.Notes.Add("坐标换算按 physical = dip * dpi / 96（四舍五入）；存取全部使用物理像素。");
        r.Notes.Add("未修改系统缩放设置；跨缩放验证依赖第二块屏幕的实际 DPI。");
        Add(r);
    }

    private static string RunDpiChild(IntPtr hwnd)
    {
        string exe = Path.GetFullPath(Path.Combine("..", "dpi-child", "bin", "Debug", "net8.0", "DpiChild.exe"));
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(hwnd.ToInt64().ToString());
        using var p = Process.Start(psi);
        string line = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        return line;
    }

    private static int DpiChild(string[] args)
    {
        IntPtr h = new IntPtr(long.Parse(args[1]));
        Native.GetWindowRect(h, out RECT r);
        Console.WriteLine($"DPICHILD|rect={r.Left},{r.Top},{r.Right},{r.Bottom}|dpi={Native.GetDpiForWindow(h)}|sys={Native.GetSystemMetrics(0)}x{Native.GetSystemMetrics(1)}");
        return 0;
    }

    // ---------------- 4. 枚举与过滤 --------------
    private static async Task ProbeEnumeration()
    {
        var r = R("P4", "窗口枚举", "PASS", "");
        int total = 0, hidden = 0, noTitle = 0, tool = 0, cloaked = 0, own = 0, zero = 0, owned = 0, usable = 0;
        var cloakedReasons = new Dictionary<int, int>();
        var sample = new List<string>();

        Native.EnumWindows((h, _) =>
        {
            total++;
            Native.GetWindowThreadProcessId(h, out uint pid);
            bool vis = Native.IsWindowVisible(h);
            long ex = Native.ExStyle(h);
            bool isTool = (ex & Native.WS_EX_TOOLWINDOW) != 0;
            bool isCloaked = Native.IsCloaked(h, out int reason);
            Native.GetWindowRect(h, out RECT wr);
            bool isZero = wr.Right - wr.Left <= 0 || wr.Bottom - wr.Top <= 0;
            bool isOwn = pid == (uint)Environment.ProcessId;

            if (!vis) hidden++;
            if (vis && Native.GetWindowTextLengthW(h) == 0) noTitle++;
            if (vis && isTool) tool++;
            if (isCloaked)
            {
                cloaked++;
                cloakedReasons[reason] = cloakedReasons.GetValueOrDefault(reason) + 1;
            }
            if (isOwn) own++;
            if (vis && isZero) zero++;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) owned++;

            bool ok = vis && !isCloaked && !isTool && !isOwn && !isZero && Native.GetWindowTextLengthW(h) > 0;
            if (ok)
            {
                usable++;
                if (sample.Count < 12)
                    sample.Add($"{Native.Title(h).Substring(0, Math.Min(28, Native.Title(h).Length))}|{Native.ClassName(h)}|pid{pid}");
            }
            return true;
        }, IntPtr.Zero);

        r.Data["total"] = total;
        r.Data["hidden"] = hidden;
        r.Data["no_title"] = noTitle;
        r.Data["toolwindow"] = tool;
        r.Data["cloaked"] = cloaked;
        r.Data["cloaked_reasons"] = cloakedReasons;
        r.Data["own_process"] = own;
        r.Data["zero_size"] = zero;
        r.Data["owned_windows"] = owned;
        r.Data["usable"] = usable;
        r.Data["sample"] = sample;

        // UWP：新开一个计算器实例；若已存在则不动（避免操作既有窗口）
        var before = ListAppFrameWindows();
        bool launched = false;
        try
        {
            var p = Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(200);
                var after = ListAppFrameWindows();
                var newOnes = after.Except(before).ToList();
                if (newOnes.Count > 0)
                {
                    launched = true;
                    var h = newOnes[0];
                    Native.GetWindowThreadProcessId(h, out uint pid);
                    string procName = "";
                    try { procName = Process.GetProcessById((int)pid).ProcessName; } catch { }
                    r.Data["uwp"] = new
                    {
                        cls = Native.ClassName(h),
                        title = Native.Title(h),
                        pid,
                        process = procName,
                        cloaked = Native.IsCloaked(h, out int cr) ? cr : 0,
                        child_cls = FindChildClass(h),
                        restore_activate_ok = ActivateAttach(h) && Native.GetForegroundWindow() == h
                    };
                    break;
                }
            }
            if (!launched) r.Data["uwp"] = "已存在计算器实例，未创建新窗口，未触碰";
        }
        catch (Exception ex) { r.Data["uwp"] = "launch failed: " + ex.Message; }

        // 最小化 -> 还原 -> 激活
        var note = Targets.FirstOrDefault(t => t.Name == "notepad-1" && t.Hwnd != IntPtr.Zero);
        if (note != null)
        {
            Native.ShowWindow(note.Hwnd, Native.SW_MINIMIZE);
            await Task.Delay(400);
            bool minimized = Native.IsIconic(note.Hwnd);
            Native.ShowWindow(note.Hwnd, Native.SW_RESTORE);
            await Task.Delay(300);
            bool restored = !Native.IsIconic(note.Hwnd);
            Native.SetForegroundWindow(note.Hwnd);
            await Task.Delay(200);
            bool fg = Native.GetForegroundWindow() == note.Hwnd;
            r.Data["minimize_restore"] = new { minimized, restored, foreground = fg };
        }

        r.Summary = $"总 {total} / 可见可用 {usable}（toolwindow {tool}, cloaked {cloaked}, 无标题 {noTitle}, 零尺寸 {zero}）";
        r.Notes.Add("过滤规则：可见 + 非 cloaked + 非 WS_EX_TOOLWINDOW + 非自身进程 + 有标题 + 非零尺寸。");
        Add(r);
    }

    private static List<IntPtr> ListAppFrameWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.ClassName(h) == "ApplicationFrameWindow") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string FindChildClass(IntPtr parent)
    {
        string found = "";
        Native.EnumWindows((h, _) => false, IntPtr.Zero); // no-op to satisfy delegate usage
        // 子窗口枚举：FindWindowEx 以 parent 为起点
        IntPtr child = FindWindowExW(parent, IntPtr.Zero, null, null);
        if (child != IntPtr.Zero) found = Native.ClassName(child);
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);

    // ---------------- 5. 原位还原 --------------
    private static async Task ProbeRestore()
    {
        var r = R("P5", "原位还原", "PASS", "");
        var errs = new List<string>();
        foreach (var t in Targets.Where(x => x.Hwnd != IntPtr.Zero))
        {
            Native.GetWindowRect(t.Hwnd, out RECT saved);
            Offset(t.Hwnd, 137, 89, 60, 40);
            await Task.Delay(300);
            Native.SetWindowPos(t.Hwnd, IntPtr.Zero, saved.Left, saved.Top,
                saved.Right - saved.Left, saved.Bottom - saved.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(300);
            Native.GetWindowRect(t.Hwnd, out RECT now);
            int e = Math.Max(Math.Max(Math.Abs(now.Left - saved.Left), Math.Abs(now.Top - saved.Top)),
                     Math.Max(Math.Abs(now.Right - saved.Right), Math.Abs(now.Bottom - saved.Bottom)));
            r.Data[t.Name] = new { saved = $"{saved.Left},{saved.Top},{saved.Right},{saved.Bottom}", now = $"{now.Left},{now.Top},{now.Right},{now.Bottom}", max_err = e };
            errs.Add($"{t.Name}={e}px");
        }

        var note = Targets.FirstOrDefault(t => t.Name == "notepad-1" && t.Hwnd != IntPtr.Zero);
        if (note != null)
        {
            Native.GetWindowRect(note.Hwnd, out RECT saved);
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            Native.GetWindowPlacement(note.Hwnd, ref wp);
            Native.ShowWindow(note.Hwnd, Native.SW_MINIMIZE);
            await Task.Delay(400);
            var wpMin = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            Native.GetWindowPlacement(note.Hwnd, ref wpMin);
            Native.ShowWindow(note.Hwnd, Native.SW_RESTORE);
            await Task.Delay(400);
            Native.GetWindowRect(note.Hwnd, out RECT afterMin);
            int em = Math.Max(Math.Abs(afterMin.Left - saved.Left), Math.Abs(afterMin.Top - saved.Top));
            Native.ShowWindow(note.Hwnd, Native.SW_MAXIMIZE);
            await Task.Delay(400);
            Native.ShowWindow(note.Hwnd, Native.SW_RESTORE);
            await Task.Delay(400);
            Native.GetWindowRect(note.Hwnd, out RECT afterMax);
            int ex = Math.Max(Math.Abs(afterMax.Left - saved.Left), Math.Abs(afterMax.Top - saved.Top));
            Native.SetWindowPos(note.Hwnd, IntPtr.Zero, saved.Left, saved.Top,
                saved.Right - saved.Left, saved.Bottom - saved.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            r.Data["minmax"] = new
            {
                min_show_cmd = wpMin.showCmd,
                normal_rc = $"{wpMin.rcNormalPosition.Left},{wpMin.rcNormalPosition.Top},{wpMin.rcNormalPosition.Right},{wpMin.rcNormalPosition.Bottom}",
                after_min_error = em,
                after_max_error = ex
            };
        }

        // 最小尺寸约束
        if (note != null)
        {
            Native.GetWindowRect(note.Hwnd, out RECT before);
            Native.SetWindowPos(note.Hwnd, IntPtr.Zero, before.Left, before.Top, 100, 80, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(300);
            Native.GetWindowRect(note.Hwnd, out RECT after);
            r.Data["min_size_clamp"] = new
            {
                requested = "100x80",
                actual = $"{after.Right - after.Left}x{after.Bottom - after.Top}"
            };
            Native.SetWindowPos(note.Hwnd, IntPtr.Zero, before.Left, before.Top,
                before.Right - before.Left, before.Bottom - before.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // 跨显示器还原
        var mons = Native.Monitors();
        var cross = Targets.FirstOrDefault(t => t.Name == "wpf-normal" && t.Hwnd != IntPtr.Zero);
        if (cross != null && mons.Count >= 2)
        {
            Native.GetWindowRect(cross.Hwnd, out RECT saved);
            var m2 = mons[1].info.rcWork;
            Native.SetWindowPos(cross.Hwnd, IntPtr.Zero, m2.Left + 100, m2.Top + 100,
                saved.Right - saved.Left, saved.Bottom - saved.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(500);
            Native.GetWindowRect(cross.Hwnd, out RECT onM2);
            Native.SetWindowPos(cross.Hwnd, IntPtr.Zero, saved.Left, saved.Top,
                saved.Right - saved.Left, saved.Bottom - saved.Top, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            await Task.Delay(500);
            Native.GetWindowRect(cross.Hwnd, out RECT back);
            int e = Math.Max(Math.Max(Math.Abs(back.Left - saved.Left), Math.Abs(back.Top - saved.Top)),
                     Math.Max(Math.Abs(back.Right - saved.Right), Math.Abs(back.Bottom - saved.Bottom)));
            r.Data["cross_monitor"] = new
            {
                on_second = $"{onM2.Left},{onM2.Top},{onM2.Right},{onM2.Bottom}",
                restored = $"{back.Left},{back.Top},{back.Right},{back.Bottom}",
                max_err = e
            };
            errs.Add($"cross={e}px");
        }
        else
        {
            r.Notes.Add("跨显示器还原：本机不满足条件（<2 台显示器）。");
            r.Data["cross_monitor"] = "N/A";
        }

        r.Summary = string.Join(", ", errs);
        r.Notes.Add("误差按四条边最大绝对差计算；还原使用 GetWindowRect（不是视觉边界）提交 SetWindowPos。");
        Add(r);
    }

    // ---------------- 6. 拖入 --------------
    private static async Task ProbeDrop()
    {
        var r = R("P6", "拖入", "PASS", "");
        var host = Targets.FirstOrDefault(t => t.Name == "wpf-normal" && t.Hwnd != IntPtr.Zero);
        if (host == null) { Add(R("P6", "拖入", "N/A", "无 WPF 宿主窗口")); return; }

        Native.OleInitialize(IntPtr.Zero);
        var target = new ProbeDropTarget();
        int hr = Native.RegisterDragDrop(host.Hwnd, target);
        r.Data["register_hresult"] = $"0x{hr:X8}";

        string temp = Path.Combine(Path.GetTempPath(), "dockprobe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string lnk = Path.Combine(temp, "notepad.lnk");
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
        shellType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { @"C:\Windows\System32\notepad.exe" });
        shellType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        if (Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
        if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);

        string exe = @"C:\Windows\System32\notepad.exe";
        IntPtr fgBefore = Native.GetForegroundWindow();
        var data = new SimpleFileDropData(new[] { lnk, exe });
        uint effect = 1;
        target.Drop((System.Runtime.InteropServices.ComTypes.IDataObject)data, 0, new Native.POINT { X = 10, Y = 10 }, ref effect);
        IntPtr fgAfter = Native.GetForegroundWindow();

        r.Data["dropped_paths"] = target.DroppedPaths;
        r.Data["dragquery_log"] = target.Log;

        if (target.DroppedPaths.Count > 0)
        {
            var resolved = ShortcutResolver.Resolve(target.DroppedPaths[0]);
            r.Data["lnk_target"] = resolved.target ?? resolved.error;
            r.Data["lnk_icon"] = ShortcutResolver.TryExtractIcon(resolved.target ?? target.DroppedPaths[0]).ok;
        }
        if (target.DroppedPaths.Count > 1)
        {
            r.Data["exe_icon"] = ShortcutResolver.TryExtractIcon(target.DroppedPaths[1]).ok;
        }
        r.Data["focus_unchanged"] = fgBefore == fgAfter;
        r.Notes.Add("Drop 由进程内构造的 CF_HDROP 数据对象直接调用，未做真实鼠标拖拽（避免接管用户鼠标）。");
        r.Notes.Add("非提权进程只能收到文件路径；提权目标（UAC）本机未构造，无法验证。");
        r.Notes.Add($"临时文件已写入 {temp}，探针退出前删除。");
        Native.RevokeDragDrop(host.Hwnd);
        try { Directory.Delete(temp, true); } catch { }
        r.Summary = $"register=0x{hr:X8}, count={target.DroppedPaths.Count}, focus_unchanged={fgBefore == fgAfter}";
        Add(r);
    }

    // ---------------- 7. 覆盖层 --------------
    private static async Task ProbeOverlay()
    {
        var r = R("P7", "覆盖层框选", "PASS", "");
        var mons = Native.Monitors();
        var m = mons[0].info;
        var note = Targets.FirstOrDefault(t => t.Name == "notepad-1" && t.Hwnd != IntPtr.Zero);
        if (note == null) { Add(R("P7", "覆盖层框选", "N/A", "无记事本靶窗口")); return; }

        var ov = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Left = 0,
            Top = 0,
            Width = 200,
            Height = 200
        };
        ov.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(ov).Handle;
            long ex = Native.ExStyle(h);
            Native.SetWindowLongPtrW(h, Native.GWL_EXSTYLE, (IntPtr)(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        };
        IntPtr fgBefore = Native.GetForegroundWindow();
        ov.Show();
        await Task.Delay(300);
        IntPtr ovH = new WindowInteropHelper(ov).Handle;
        Native.SetWindowPos(ovH, new IntPtr(-1), m.rcMonitor.Left, m.rcMonitor.Top,
            m.rcMonitor.Right - m.rcMonitor.Left, m.rcMonitor.Bottom - m.rcMonitor.Top, 0x0040 | 0x0010);
        await Task.Delay(600);
        IntPtr fgAfter = Native.GetForegroundWindow();

        Native.GetWindowRect(ovH, out RECT ovRect);
        Native.GetWindowRect(note.Hwnd, out RECT nr);
        var center = new Native.POINT { X = (nr.Left + nr.Right) / 2, Y = (nr.Top + nr.Bottom) / 2 };
        IntPtr under = Native.WindowFromPoint(center);

        var any = ZOrderHitTest(SelRect(nr, -4), false);
        var half = ZOrderHitTest(SelRect(nr, -4), true);

        // click-through：加 WS_EX_TRANSPARENT 后同一点应落到记事本
        long ex2 = Native.ExStyle(ovH);
        Native.SetWindowLongPtrW(ovH, Native.GWL_EXSTYLE, (IntPtr)(ex2 | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED));
        await Task.Delay(300);
        IntPtr through = Native.WindowFromPoint(center);
        Native.GetWindowThreadProcessId(through, out uint throughPid);

        r.Data["overlay_rect"] = $"{ovRect.Left},{ovRect.Top},{ovRect.Right},{ovRect.Bottom}";
        r.Data["foreground_unchanged"] = fgBefore == fgAfter;
        r.Data["foreground_before"] = fgBefore.ToInt64();
        r.Data["foreground_after"] = fgAfter.ToInt64();
        Native.GetWindowThreadProcessId(under, out uint underPid);
        Native.GetWindowThreadProcessId(note.Hwnd, out uint notePid);
        r.Data["window_from_point_over_overlay"] = new { cls = Native.ClassName(under), pid = underPid, is_overlay = under == ovH };
        r.Data["window_from_point_clickthrough"] = new { cls = Native.ClassName(through), pid = throughPid, is_overlay = through == ovH, target_pid = notePid };
        r.Data["hit_any"] = any.Select(h => Native.Title(h)).ToList();
        r.Data["hit_any_count"] = any.Count;
        r.Data["hit_half"] = half.Select(h => Native.Title(h)).ToList();
        r.Data["hit_half_count"] = half.Count;
        r.Data["expected_hit"] = new[] { Native.Title(note.Hwnd) };

        ov.Close();
        await Task.Delay(200);

        r.Summary = $"fg_unchanged={fgBefore == fgAfter}, hit_any={any.Count}, hit_half={half.Count}, clickthrough={through != ovH}";
        r.Notes.Add("框选矩形由代码注入（未做真实鼠标拖拽）；命中判定用 EnumWindows 的 z-order + EXTENDED_FRAME_BOUNDS 求交。");
        r.Notes.Add("覆盖层覆盖主屏；跨混合 DPI 的双屏覆盖未验证。");
        Add(r);
    }

    private static RECT SelRect(RECT r, int inflate) => new RECT
    {
        Left = r.Left - inflate,
        Top = r.Top - inflate,
        Right = r.Right + inflate,
        Bottom = r.Bottom + inflate
    };

    private static List<IntPtr> ZOrderHitTest(RECT sel, bool half)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (Native.GetWindowTextLengthW(h) == 0) return true;
            if ((Native.ExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.IsCloaked(h, out int cloakedReason)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return true;
            RECT vr = Native.VisualRect(h);
            long wa = Area(vr);
            long ia = Area(Intersect(vr, sel));
            if (wa <= 0 || ia <= 0) return true;
            if (half && ia < wa / 2) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static long Area(RECT r) => r.Right <= r.Left || r.Bottom <= r.Top ? 0 : (long)(r.Right - r.Left) * (r.Bottom - r.Top);

    private static RECT Intersect(RECT a, RECT b) => new RECT
    {
        Left = Math.Max(a.Left, b.Left),
        Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right),
        Bottom = Math.Min(a.Bottom, b.Bottom)
    };

    // ---------------- 8. 全局热键 --------------
    private static async Task ProbeHotkey()
    {
        var r = R("P8", "全局热键", "PASS", "");
        var src = new HwndSource(new HwndSourceParameters("ProbeHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)Native.WS_EX_TOOLWINDOW
        });
        IntPtr hwnd = src.Handle;

        bool k1 = Native.RegisterHotKey(hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_F10);
        int e1 = Marshal.GetLastWin32Error();
        bool k2 = Native.RegisterHotKey(hwnd, 2, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_F11);
        int e2 = Marshal.GetLastWin32Error();
        bool k3 = Native.RegisterHotKey(hwnd, 3, Native.MOD_WIN | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_OEM_3);
        int e3 = Marshal.GetLastWin32Error();

        var child = ClaimHotkey();
        bool reOk = Native.RegisterHotKey(hwnd, 4, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_F10);
        int e4 = Marshal.GetLastWin32Error();
        Native.UnregisterHotKey(hwnd, 1);
        Native.UnregisterHotKey(hwnd, 2);
        Native.UnregisterHotKey(hwnd, 3);
        Native.UnregisterHotKey(hwnd, 4);
        bool reAfter = Native.RegisterHotKey(hwnd, 5, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_F10);
        Native.UnregisterHotKey(hwnd, 5);
        src.Dispose();

        int pt = Process.GetProcesses().Count(p => p.ProcessName.StartsWith("PowerToys", StringComparison.OrdinalIgnoreCase));
        r.Data["ctrl_alt_shift_f10"] = new { ok = k1, err = e1 };
        r.Data["ctrl_alt_shift_f11"] = new { ok = k2, err = e2 };
        r.Data["win_shift_backtick"] = new { ok = k3, err = e3 };
        r.Data["child_same_combo"] = child;
        r.Data["duplicate_same_window"] = new { ok = reOk, err = e4 };
        r.Data["re_register_after_unregister"] = reAfter;
        r.Data["powertoys_processes"] = pt;
        r.Summary = $"f10={k1}(err {e1}), f11={k2}(err {e2}), win+shift+`={k3}(err {e3}), conflict_child={child}, after_unregister={reAfter}";
        r.Notes.Add("所有注册在探针内均已注销；未注册开机项、未改系统设置。");
        Add(r);
        await Task.CompletedTask;
    }

    private static string ClaimHotkey()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("--claim-hotkey");
        using var p = Process.Start(psi);
        string line = p.StandardOutput.ReadLine() ?? "";
        p.WaitForExit(5000);
        return line.Trim();
    }

    private static int ClaimHotkeyChild()
    {
        bool ok = Native.RegisterHotKey(IntPtr.Zero, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, Native.VK_F10);
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"HOTKEYCHILD|ok={ok}|err={err}");
        if (ok) Native.UnregisterHotKey(IntPtr.Zero, 1);
        return 0;
    }

}
