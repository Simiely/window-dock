using System;
using System.Runtime.InteropServices;
using System.Text;

// 独立子进程：故意“DPI 不感知”，用来验证宿主把物理像素坐标交给未感知进程时会发生什么。
// 用法：DpiChild.exe <hwnd>
// 输出：DPICHILD|rect=L,T,R,B|dpi=<dpi>|sys=<SM_CXSCREEN>x<SM_CYSCREEN>|aware=<模式>
internal static class DpiChild
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr ctx);

    private static int Main(string[] args)
    {
        if (args.Length < 1) return 2;
        IntPtr h = new IntPtr(long.Parse(args[0]));
        if (!GetWindowRect(h, out RECT r)) return 3;
        int aware = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
        var sb = new StringBuilder();
        sb.Append($"DPICHILD|rect={r.Left},{r.Top},{r.Right},{r.Bottom}");
        sb.Append($"|dpi={GetDpiForWindow(h)}");
        sb.Append($"|sys={GetSystemMetrics(0)}x{GetSystemMetrics(1)}");
        sb.Append($"|aware={aware}");
        Console.WriteLine(sb.ToString());
        return 0;
    }
}
