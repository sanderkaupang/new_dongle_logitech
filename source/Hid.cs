using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>Minimal Win32 HID access: finds the vendor (HID++) collections of a USB device.</summary>
static class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    public const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int count, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int count, out int written, IntPtr overlapped);
}

/// <summary>
/// One HID++ endpoint. On Windows, short (7 byte) and long (20 byte) reports live in separate
/// HID collections, so each kind has its own handles. Reads and writes use separate handles
/// because synchronous I/O on a single handle would make writes wait for the blocking read.
/// </summary>
sealed class HidppPort
{
    readonly SafeFileHandle? _shortWrite, _longWrite;
    readonly BlockingCollection<byte[]> _incoming = new();
    readonly List<byte[]> _backlog = new();

    public volatile bool IsAlive = true;

    HidppPort(SafeFileHandle? shortWrite, SafeFileHandle? longWrite)
    {
        _shortWrite = shortWrite;
        _longWrite = longWrite;
    }

    public static HidppPort? Open(ushort pid)
    {
        SafeFileHandle? shortWrite = null, longWrite = null;
        var readers = new List<(SafeFileHandle Handle, int Length)>();

        foreach (var path in EnumerateHidPaths($"vid_046d&pid_{pid:x4}"))
        {
            using var probe = OpenPath(path);
            if (probe.IsInvalid) continue;
            if (!TryGetCaps(probe, out var caps) || caps.UsagePage < 0xFF00) continue;
            if (caps.OutputReportByteLength != 7 && caps.OutputReportByteLength != 20) continue;

            var write = OpenPath(path);
            var read = OpenPath(path);
            if (write.IsInvalid || read.IsInvalid) continue;

            if (caps.OutputReportByteLength == 7) shortWrite = write; else longWrite = write;
            readers.Add((read, caps.InputReportByteLength));
        }

        if (shortWrite == null && longWrite == null) return null;

        var port = new HidppPort(shortWrite, longWrite);
        foreach (var (handle, length) in readers)
            new Thread(() => port.ReadLoop(handle, length)) { IsBackground = true }.Start();
        return port;
    }

    void ReadLoop(SafeFileHandle handle, int length)
    {
        var buffer = new byte[length];
        while (Native.ReadFile(handle, buffer, length, out int read, IntPtr.Zero))
            if (read > 0) _incoming.Add(buffer[..read]);
        IsAlive = false; // the read only fails when the device has gone away
        handle.Dispose();
    }

    /// <summary>Product IDs of every Logitech HID device currently plugged in.</summary>
    public static HashSet<ushort> PresentPids()
    {
        var pids = new HashSet<ushort>();
        foreach (var path in EnumerateHidPaths("vid_046d&pid_"))
        {
            var at = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at + 8 <= path.Length &&
                ushort.TryParse(path.AsSpan(at + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var pid))
                pids.Add(pid);
        }
        return pids;
    }

    /// <summary>Sends a frame whose first byte is the report ID (0x10 short, 0x11 long).</summary>
    public void Write(byte[] frame)
    {
        var handle = frame[0] == 0x10 ? _shortWrite : _longWrite;
        if (handle == null) throw new InvalidOperationException($"Device has no collection for report 0x{frame[0]:X2}");
        if (!Native.WriteFile(handle, frame, frame.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "HID write failed");
    }

    /// <summary>Next incoming frame (frames set aside by Request come first), or null on timeout.</summary>
    public byte[]? Next(int timeoutMs)
    {
        if (_backlog.Count > 0)
        {
            var frame = _backlog[0];
            _backlog.RemoveAt(0);
            return frame;
        }
        return _incoming.TryTake(out var next, timeoutMs) ? next : null;
    }

    /// <summary>Writes a frame and waits for the first reply accepted by <paramref name="isReply"/>.
    /// Anything else (e.g. notifications) is kept for later Next() calls.</summary>
    public byte[]? Request(byte[] frame, Func<byte[], bool> isReply, int timeoutMs = 1500)
    {
        Write(frame);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0 || !_incoming.TryTake(out var reply, remaining)) return null;
            if (isReply(reply)) return reply;
            _backlog.Add(reply);
        }
    }

    /// <summary>Discards everything received so far, so old messages can't be mistaken for new ones.</summary>
    public void Drain()
    {
        _backlog.Clear();
        while (_incoming.TryTake(out _)) { }
    }

    static SafeFileHandle OpenPath(string path) => Native.CreateFileW(path,
        Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
        IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

    static bool TryGetCaps(SafeFileHandle handle, out Native.HIDP_CAPS caps)
    {
        caps = default;
        if (!Native.HidD_GetPreparsedData(handle, out var data)) return false;
        try { return Native.HidP_GetCaps(data, out caps) == 0x00110000; } // HIDP_STATUS_SUCCESS
        finally { Native.HidD_FreePreparsedData(data); }
    }

    static List<string> EnumerateHidPaths(string filter)
    {
        var paths = new List<string>();
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        try
        {
            var iface = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface); i++)
            {
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of the fixed part
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    if (path.Contains(filter, StringComparison.OrdinalIgnoreCase)) paths.Add(path);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }
}
