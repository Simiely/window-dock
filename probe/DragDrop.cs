using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace ProbeHost;

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    void DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, uint grfKeyState, [In] Native.POINT pt, ref uint pdwEffect);
    void DragOver(uint grfKeyState, [In] Native.POINT pt, ref uint pdwEffect);
    void DragLeave();
    void Drop([MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj, uint grfKeyState, [In] Native.POINT pt, ref uint pdwEffect);
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class SimpleFileDropData : IDataObject
{
    private readonly IntPtr _hGlobal;
    private readonly List<string> _paths;

    public SimpleFileDropData(IEnumerable<string> paths)
    {
        _paths = paths.ToList();
        _hGlobal = BuildHDrop(_paths);
    }

    private static IntPtr BuildHDrop(List<string> paths)
    {
        int header = Marshal.SizeOf<DropFilesHeader>();
        var sb = new StringBuilder();
        foreach (var p in paths) sb.Append(p).Append('\0');
        sb.Append('\0');
        int chars = sb.Length;
        int size = header + chars * 2;
        IntPtr h = Native.GlobalAlloc(0x0002 | 0x0040, (UIntPtr)size);
        IntPtr p2 = Native.GlobalLock(h);
        try
        {
            var hdr = new DropFilesHeader { pFiles = (uint)header, fWide = 1 };
            Marshal.StructureToPtr(hdr, p2, false);
            var bytes = Encoding.Unicode.GetBytes(sb.ToString());
            Marshal.Copy(bytes, 0, p2 + header, bytes.Length);
        }
        finally { Native.GlobalUnlock(h); }
        return h;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (format.cfFormat != 15) throw new COMException("only CF_HDROP", unchecked((int)0x80040064));
        medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = _hGlobal, pUnkForRelease = null };
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotImplementedException();
    public int QueryGetData(ref FORMATETC format) => format.cfFormat == 15 ? 0 : unchecked((int)0x80040064);
    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return unchecked((int)0x80040003); }
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotImplementedException();
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotImplementedException();
    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) { }
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }

    public IReadOnlyList<string> Paths => _paths;

    [StructLayout(LayoutKind.Sequential)]
    private struct DropFilesHeader { public uint pFiles; public int x; public int y; public int fNC; public int fWide; }
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ProbeDropTarget : IDropTarget
{
    public readonly List<string> DroppedPaths = new();
    public readonly List<string> Log = new();

    public void DragEnter(IDataObject data, uint keyState, Native.POINT pt, ref uint effect) { effect = 1; }
    public void DragOver(uint keyState, Native.POINT pt, ref uint effect) { effect = 1; }
    public void DragLeave() { }

    public void Drop(IDataObject data, uint keyState, Native.POINT pt, ref uint effect)
    {
        effect = 1;
        var fmt = new FORMATETC
        {
            cfFormat = 15,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
        data.GetData(ref fmt, out STGMEDIUM medium);
        try
        {
            IntPtr hdrop = medium.unionmember;
            uint count = Native.DragQueryFileW(hdrop, 0xFFFFFFFF, null, 0);
            Log.Add($"DragQueryFile count={count}");
            for (uint i = 0; i < count; i++)
            {
                var sb = new StringBuilder(1024);
                Native.DragQueryFileW(hdrop, i, sb, (uint)sb.Capacity);
                DroppedPaths.Add(sb.ToString());
            }
        }
        finally { Native.ReleaseStgMedium(ref medium); }
    }
}

internal static class ShortcutResolver
{
    public static (string target, string iconLocation, string error) Resolve(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (path, path, null);
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            string target = (string)t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
            string icon = (string)t.InvokeMember("IconLocation", BindingFlags.GetProperty, null, link, null);
            if (Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            if (Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            return (target, icon, null);
        }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    public static (bool ok, string error) TryExtractIcon(string path)
    {
        try
        {
            var info = new Native.SHFILEINFO();
            uint r = (uint)Native.SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<Native.SHFILEINFO>(), 0x100 | 0x0);
            if (info.hIcon != IntPtr.Zero) Native.DestroyIcon(info.hIcon);
            return (r != 0 && info.hIcon != IntPtr.Zero, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
