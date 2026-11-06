using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using static ProbeHost.Native;

namespace ProbeHost;

/// <summary>
/// 只读探针：不创建窗口、不移动窗口、不注册热键、不发送输入。
/// 只调用 EnumWindows / GetWindowRect / DwmGetWindowAttribute / GetDpiForWindow 等读取型 API。
/// </summary>
public static class ReadOnlyProbe
{
    public static void Run()
    {
        Directory.CreateDirectory("out");
        var log = new StringBuilder();

        void Say(string s) { Console.WriteLine(s); log.AppendLine(s); }

        Say($"READONLY PROBE {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"os={Environment.OSVersion.Version} build={Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "")} 64bit={Environment.Is64BitProcess}");

        var mon = Monitors();
        Say("");
        Say("[显示器]");
        foreach (var m in mon) Say($"  {m.device} rect={m.rect} work={m.work} dpi={m.dpi} scale={m.scale}% dpiWindow={m.dpiWindow}");

        var en = Enumeration();
        Say("");
        Say($"[枚举] total={en.total} usable={en.usable} hidden={en.hidden} noTitle={en.noTitle} tool={en.tool} cloaked={en.cloaked} own={en.own} zero={en.zero} owned={en.owned}");
        Say($"  cloaked reasons: {string.Join(", ", en.cloakedReasons.Select(kv => $"0x{kv.Key:X}:{kv.Value}"))}");
        if (en.uwp.Count > 0)
            Say($"  UWP host: {string.Join(" | ", en.uwp.Select(u => $"{u.title} pid={u.pid} proc={u.process} child={u.childClass}"))}");
        else
            Say("  UWP host: 0 个 ApplicationFrameWindow");

        var rects = Rects();
        Say("");
        Say("[矩形差 GetWindowRect - EXTENDED_FRAME_BOUNDS]");
        foreach (var s in rects.Stats) Say($"  {s.category,-18} n={s.n,-3} L[{s.minL},{s.maxL}] T[{s.minT},{s.maxT}] R[{s.minR},{s.maxR}] B[{s.minB},{s.maxB}] exact0={s.exact0}");
        Say("  明细:");
        foreach (var d in rects.Detail.Take(15))
            Say($"    {d.title,-28} cls={d.cls,-32} d=({d.dl},{d.dt},{d.dr},{d.db}) win={d.w}x{d.h}");

        var minz = MinimizedCheck();
        Say("");
        Say($"[最小化窗口] iconic 可见窗口={minz.iconic}；GetWindowRect 落在屏幕外哨兵={minz.sentinel}；样本:");
        foreach (var s in minz.samples)
            Say($"    {s.title,-24} rect={s.rect,-28} rcNormal={s.normal} showCmd={s.showCmd}");

        var dpi = DpiCheck(mon);
        Say("");
        Say($"[DPI] {dpi.summary}");
        Say($"  formula: physical = round(dip * dpi / 96); dip = round(physical * 96 / dpi); dpi={dpi.dpi} scale={dpi.scale}%");

        var perm = Permission();
        Say("");
        Say($"[权限] selfElevated={perm.selfElevated} enableLUA={perm.enableLua} consent={perm.consentPromptBehaviorAdmin} secureDesktop={perm.promptOnSecureDesktop} elevatedVisibleWindows={perm.elevatedWindows}");

        var vd = VirtualDesktopCheck();
        Say("");
        Say($"[虚拟桌面] cloakedShell(=0x2)={vd.cloakedShell} 其中不在当前桌面={vd.offCurrent} 在公共桌面/无法判定={vd.unknown} err={vd.error}");

        var hk = HotkeyInventory();
        Say("");
        Say($"[热键占用（只读推断，未注册任何热键）] PowerToys 进程: {string.Join(", ", hk.powerToysProcesses)}");
        foreach (var x in hk.items) Say($"  {x.module,-16} {x.combo,-24} <- {x.name}");
        if (hk.items.Count == 0) Say("  未在 PowerToys 配置中找到可读的热键组合");

        var json = JsonSerializer.Serialize(new { generated = DateTime.Now, monitors = mon, enumeration = en, rectStats = rects.Stats, rectDetail = rects.Detail, minimized = minz, dpi, permission = perm, virtualDesktop = vd, hotkeys = hk }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true });
        File.WriteAllText("out/readonly.json", json);
        File.WriteAllText("out/readonly-console.txt", log.ToString());
        Say("");
        Say("WROTE out/readonly.json");
    }

    // ---------- 显示器 ----------
    private sealed class MonInfo
    {
        public string device; public string rect; public string work; public uint dpi; public int scale; public uint dpiWindow; public string dpiWindowWindow;
    }

    private static List<MonInfo> Monitors()
    {
        var list = new List<MonInfo>();
        var visible = UsableWindows().Where(w => !w.cloaked).ToList();
        foreach (var (h, mi, dpi) in Native.Monitors())
        {
            var win = visible.FirstOrDefault(w =>
            {
                int cx = (w.rect.Left + w.rect.Right) / 2, cy = (w.rect.Top + w.rect.Bottom) / 2;
                return cx >= mi.rcMonitor.Left && cx < mi.rcMonitor.Right && cy >= mi.rcMonitor.Top && cy < mi.rcMonitor.Bottom;
            });
            list.Add(new MonInfo
            {
                device = mi.szDevice,
                rect = $"{mi.rcMonitor.Left},{mi.rcMonitor.Top},{mi.rcMonitor.Right},{mi.rcMonitor.Bottom}",
                work = $"{mi.rcWork.Left},{mi.rcWork.Top},{mi.rcWork.Right},{mi.rcWork.Bottom}",
                dpi = dpi,
                scale = Native.ScalePercent(dpi),
                dpiWindow = win != null ? Native.GetDpiForWindow(win.h) : 0,
                dpiWindowWindow = win?.title ?? ""
            });
        }
        return list;
    }

    // ---------- 枚举 ----------
    private sealed class WinRow
    {
        public IntPtr h; public string title; public string cls; public uint pid; public string process;
        public long ex; public RECT rect; public bool cloaked; public int cloakReason; public bool elevated;
    }

    private sealed class EnumResult
    {
        public int total, usable, hidden, noTitle, tool, cloaked, own, zero, owned;
        public Dictionary<int, int> cloakedReasons = new();
        public List<UwpRow> uwp = new();
    }

    private sealed class UwpRow
    {
        public string title; public uint pid; public string process; public string childClass; public uint dpi;
    }

    private static EnumResult Enumeration()
    {
        var res = new EnumResult();
        var rows = new List<WinRow>();
        Native.EnumWindows((h, _) =>
        {
            res.total++;
            Native.GetWindowThreadProcessId(h, out uint pid);
            bool vis = Native.IsWindowVisible(h);
            long ex = Native.ExStyle(h);
            bool isTool = (ex & Native.WS_EX_TOOLWINDOW) != 0;
            bool cloaked = Native.IsCloaked(h, out int reason);
            Native.GetWindowRect(h, out RECT wr);
            bool isZero = wr.Right - wr.Left <= 0 || wr.Bottom - wr.Top <= 0;
            bool isOwn = pid == (uint)Environment.ProcessId;
            string cls = Native.ClassName(h);
            string title = Native.Title(h);

            if (!vis) res.hidden++;
            if (vis && title.Length == 0) res.noTitle++;
            if (vis && isTool) res.tool++;
            if (cloaked)
            {
                res.cloaked++;
                res.cloakedReasons[reason] = res.cloakedReasons.GetValueOrDefault(reason) + 1;
            }
            if (isOwn) res.own++;
            if (vis && isZero) res.zero++;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) res.owned++;

            if (vis && !cloaked && !isTool && !isOwn && !isZero && title.Length > 0)
            {
                res.usable++;
                rows.Add(new WinRow { h = h, title = title, cls = cls, pid = pid, process = ProcName(pid), ex = ex, rect = wr, cloaked = cloaked, cloakReason = reason });
            }
            return true;
        }, IntPtr.Zero);

        // 提权窗口（只读，不触碰）
        foreach (var r in rows) r.elevated = Native.IsElevatedPid(r.pid);

        // UWP / 打包应用识别（按窗口类 + 宿主进程）
        foreach (var r in rows.Where(r => r.cls == "ApplicationFrameWindow"))
        {
            res.uwp.Add(new UwpRow
            {
                title = r.title,
                pid = r.pid,
                process = r.process,
                childClass = FindChildClass(r.h),
                dpi = Native.GetDpiForWindow(r.h)
            });
        }

        return res;
    }

    private static List<WinRow> UsableWindows()
    {
        var list = new List<WinRow>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!Native.IsWindowVisible(h)) return true;
            if (pid == (uint)Environment.ProcessId) return true;
            if (Native.GetWindowTextLengthW(h) == 0) return true;
            if ((Native.ExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            Native.GetWindowRect(h, out RECT wr);
            if (wr.Right - wr.Left <= 0) return true;
            list.Add(new WinRow
            {
                h = h,
                title = Native.Title(h),
                cls = Native.ClassName(h),
                pid = pid,
                process = ProcName(pid),
                ex = Native.ExStyle(h),
                rect = wr,
                cloaked = Native.IsCloaked(h, out int reason),
                cloakReason = reason
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string FindChildClass(IntPtr parent)
    {
        IntPtr child = FindWindowExW(parent, IntPtr.Zero, null, null);
        return child == IntPtr.Zero ? "" : Native.ClassName(child);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);

    private static string ProcName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return ""; }
    }

    // ---------- 矩形差 ----------
    private sealed class RectDetail
    {
        public string title, cls, category, process; public int dl, dt, dr, db, w, h; public bool maximized;
    }

    private sealed class RectStat
    {
        public string category; public int n, minL, maxL, minT, maxT, minR, maxR, minB, maxB, exact0;
    }

    private static (List<RectDetail> Detail, List<RectStat> Stats) Rects()
    {
        var detail = new List<RectDetail>();
        foreach (var w in UsableWindows().Where(x => !x.cloaked).Take(40))
        {
            Native.GetWindowRect(w.h, out RECT wr);
            RECT vr = Native.VisualRect(w.h);
            detail.Add(new RectDetail
            {
                title = w.title.Length > 34 ? w.title[..34] : w.title,
                cls = w.cls,
                category = Category(w.cls),
                process = w.process,
                dl = wr.Left - vr.Left,
                dt = wr.Top - vr.Top,
                dr = wr.Right - vr.Right,
                db = wr.Bottom - vr.Bottom,
                w = wr.Right - wr.Left,
                h = wr.Bottom - wr.Top,
                maximized = Native.IsZoomed(w.h)
            });
        }

        var stats = detail.GroupBy(d => d.category).Select(g =>
        {
            var items = g.ToList();
            return new RectStat
            {
                category = g.Key,
                n = items.Count,
                minL = items.Min(x => x.dl), maxL = items.Max(x => x.dl),
                minT = items.Min(x => x.dt), maxT = items.Max(x => x.dt),
                minR = items.Min(x => x.dr), maxR = items.Max(x => x.dr),
                minB = items.Min(x => x.db), maxB = items.Max(x => x.db),
                exact0 = items.Count(x => x.dl == 0 && x.dt == 0 && x.dr == 0 && x.db == 0)
            };
        }).OrderByDescending(s => s.n).ToList();
        return (detail, stats);
    }

    private static string Category(string cls) => cls switch
    {
        "ApplicationFrameWindow" => "UWP host",
        "WinUIDesktopWin32WindowClass" => "WinUI3",
        "Chrome_WidgetWin_1" => "Chromium/Electron",
        "MozillaWindowClass" => "Firefox",
        "CASCADIA_HOSTING_WINDOW_CLASS" => "Terminal",
        "#32770" => "Dialog",
        "CabinetWClass" => "Explorer",
        "Notepad" => "Notepad",
        _ => "Other"
    };

    // ---------- DPI ----------
    private sealed class DpiResult
    {
        public string summary; public uint dpi; public int scale;
        public object samples;
    }

    private static DpiResult DpiCheck(List<MonInfo> mons)
    {
        var win = UsableWindows().FirstOrDefault(w => !w.cloaked);
        if (win == null) return new DpiResult { summary = "no visible window to read dpi from", dpi = 0, scale = 0, samples = null };

        uint dpi = Native.GetDpiForWindow(win.h);
        var topLeft = new Native.POINT { X = win.rect.Left, Y = win.rect.Top };
        bool ok = Native.PhysicalToLogicalPointForPerMonitorDPI(win.h, ref topLeft);
        int expect = (int)Math.Round(win.rect.Left * 96.0 / dpi);
        return new DpiResult
        {
            dpi = dpi,
            scale = Native.ScalePercent(dpi),
            summary = $"window='{win.title}' dpi={dpi} scale={Native.ScalePercent(dpi)}% physical_left={win.rect.Left} p2l_ok={ok} logical_left={topLeft.X} expected={expect}",
            samples = new
            {
                window = win.title,
                class_ = win.cls,
                physicalRect = $"{win.rect.Left},{win.rect.Top},{win.rect.Right},{win.rect.Bottom}",
                dpi,
                scalePercent = Native.ScalePercent(dpi),
                physicalLeft = win.rect.Left,
                logicalLeftFromApi = topLeft.X,
                expectedDipRounded = expect,
                monitorDpis = mons.Select(m => new { m.device, m.dpi, m.dpiWindow, m.scale }).ToList()
            }
        };
    }

    // ---------- 权限 ----------
    private sealed class PermResult
    {
        public bool selfElevated; public int enableLua; public int consentPromptBehaviorAdmin; public int promptOnSecureDesktop;
        public int elevatedWindows; public string uiAccessNote;
    }

    private static PermResult Permission()
    {
        bool elevated = false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { }

        int enableLua = -1, consent = -1, secure = -1;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            enableLua = (int)(k?.GetValue("EnableLUA") ?? -1);
            consent = (int)(k?.GetValue("ConsentPromptBehaviorAdmin") ?? -1);
            secure = (int)(k?.GetValue("PromptOnSecureDesktop") ?? -1);
        }
        catch { }

        int elevatedWindows = 0;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid != (uint)Environment.ProcessId && Native.IsWindowVisible(h) && Native.IsElevatedPid(pid)) elevatedWindows++;
            return true;
        }, IntPtr.Zero);

        return new PermResult
        {
            selfElevated = elevated,
            enableLua = enableLua,
            consentPromptBehaviorAdmin = consent,
            promptOnSecureDesktop = secure,
            elevatedWindows = elevatedWindows,
            uiAccessNote = "UIPI 生效与否由 EnableLUA 与完整性级别决定；实际能否移动/激活提权窗口必须交互实测，只读无法确认。"
        };
    }

    // ---------- 最小化窗口 ----------
    private sealed class MinResult
    {
        public int iconic; public int sentinel; public List<MinSample> samples = new();
    }

    private sealed class MinSample { public string title; public string rect; public string normal; public int showCmd; }

    private static MinResult MinimizedCheck()
    {
        var res = new MinResult();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsIconic(h)) return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Environment.ProcessId) return true;
            if (Native.GetWindowTextLengthW(h) == 0) return true;

            res.iconic++;
            Native.GetWindowRect(h, out RECT r);
            if (r.Left <= -32000 || r.Top <= -32000) res.sentinel++;

            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            Native.GetWindowPlacement(h, ref wp);
            if (res.samples.Count < 6)
            {
                res.samples.Add(new MinSample
                {
                    title = Native.Title(h),
                    rect = $"{r.Left},{r.Top},{r.Right},{r.Bottom}",
                    normal = $"{wp.rcNormalPosition.Left},{wp.rcNormalPosition.Top},{wp.rcNormalPosition.Right},{wp.rcNormalPosition.Bottom}",
                    showCmd = wp.showCmd
                });
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    // ---------- 虚拟桌面（只读） ----------
    private sealed class VdResult { public int cloakedShell; public int offCurrent; public int unknown; public string error; }

    [ComImport, Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManagerClsid { }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    private static VdResult VirtualDesktopCheck()
    {
        var res = new VdResult();
        IVirtualDesktopManager vdm;
        try { vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClsid(); }
        catch (Exception ex) { res.error = "CoCreateInstance 失败: " + ex.Message; return res; }

        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            if (!Native.IsCloaked(h, out int reason) || reason != 0x2) return true;
            res.cloakedShell++;
            try
            {
                // 只读调用：不切换桌面、不移动窗口
                int hr = vdm.IsWindowOnCurrentVirtualDesktop(h, out int onCurrent);
                if (hr == 0) { if (onCurrent == 0) res.offCurrent++; }
                else res.unknown++;
            }
            catch { res.unknown++; }
            return true;
        }, IntPtr.Zero);

        return res;
    }

    // ---------- 热键（只读推断） ----------
    private sealed class HkResult
    {
        public List<HkItem> items = new();
        public List<string> powerToysProcesses = new();
    }

    private sealed class HkItem { public string module; public string name; public string combo; public string raw; }

    private static HkResult HotkeyInventory()
    {
        var r = new HkResult();
        try
        {
            r.powerToysProcesses = Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith("PowerToys", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.ProcessName).Distinct().ToList();
        }
        catch { }

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "PowerToys");
        if (!Directory.Exists(root)) return r;

        foreach (var f in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var text = File.ReadAllText(f);
                if (!text.Contains("hotkey", StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains("shortcut", StringComparison.OrdinalIgnoreCase)) continue;

                string rel = Path.GetRelativePath(root, f);
                string module = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (module.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) module = "PowerToys";

                using var doc = JsonDocument.Parse(text);
                var leaves = Flatten(doc.RootElement).ToList();

                // 把 win/ctrl/alt/shift/code 这五个兄弟叶子合并成一个组合
                var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, value) in leaves)
                {
                    int i = name.LastIndexOf('.');
                    if (i < 0) continue;
                    string parent = name[..i];
                    string leaf = name[(i + 1)..];
                    if (leaf is not ("win" or "ctrl" or "alt" or "shift" or "code")) continue;
                    if (!parent.Contains("hotkey", StringComparison.OrdinalIgnoreCase) &&
                        !parent.Contains("shortcut", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!groups.TryGetValue(parent, out var g))
                        groups[parent] = g = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    g[leaf] = value;
                }

                foreach (var kv in groups)
                {
                    if (!kv.Value.TryGetValue("code", out string codeText)) continue;
                    if (!int.TryParse(codeText, out int code) || code == 0) continue;

                    string combo = BuildCombo(
                        kv.Value.TryGetValue("win", out string w) && w.Equals("true", StringComparison.OrdinalIgnoreCase),
                        kv.Value.TryGetValue("ctrl", out string c) && c.Equals("true", StringComparison.OrdinalIgnoreCase),
                        kv.Value.TryGetValue("alt", out string a) && a.Equals("true", StringComparison.OrdinalIgnoreCase),
                        kv.Value.TryGetValue("shift", out string s) && s.Equals("true", StringComparison.OrdinalIgnoreCase),
                        code);

                    r.items.Add(new HkItem { module = module, name = kv.Key, combo = combo, raw = string.Join(",", kv.Value.Select(x => $"{x.Key}={x.Value}")) });
                }
            }
            catch { }
        }

        r.items = r.items
            .GroupBy(x => x.module + "|" + x.combo)
            .Select(g => g.First())
            .OrderBy(x => x.module).ThenBy(x => x.combo)
            .ToList();
        return r;
    }

    private static string BuildCombo(bool win, bool ctrl, bool alt, bool shift, int code)
    {
        var parts = new List<string>();
        if (win) parts.Add("Win");
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(VkName(code));
        return string.Join("+", parts);
    }

    private static string VkName(int code) => code switch
    {
        0x20 => "Space", 0x21 => "PageUp", 0x22 => "PageDown",
        0x2E => "Delete", 0xBB => "=", 0xBD => "-", 0xBF => "/",
        0xC0 => "`", 0xDE => "'", 0xBE => ".",
        0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
        0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
        0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
        >= 0x30 and <= 0x39 => ((char)code).ToString(),
        >= 0x41 and <= 0x5A => ((char)code).ToString(),
        _ => $"VK{code:X2}"
    };

    private static IEnumerable<(string name, string value)> Flatten(JsonElement e, string prefix = "")
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                foreach (var x in Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name)) yield return x;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var v in e.EnumerateArray())
            {
                foreach (var x in Flatten(v, prefix + $"[{i++}]")) yield return x;
            }
        }
        else yield return (prefix, e.ToString());
    }
}
