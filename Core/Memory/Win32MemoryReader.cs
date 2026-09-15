using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MnmDamageParser.Core.Memory;

/// <summary>
/// Real ReadProcessMemory/VirtualQueryEx-backed IMemoryReader. Windows-only
/// at runtime (P/Invoke declarations still *compile* fine on any OS -- they
/// just throw/fail if actually called on a non-Windows host, which never
/// happens in practice here). This is the C# equivalent of memlib.py.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Win32MemoryReader : IMemoryReader
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private static readonly HashSet<uint> WritableProtect = new() { 0x04, 0x08, 0x40, 0x80 }; // RW, WRITECOPY, EXEC_RW, EXEC_WRITECOPY

    private IntPtr _handle = IntPtr.Zero;
    private int _pid;

    public bool IsAlive
    {
        get
        {
            if (_handle == IntPtr.Zero) return false;
            if (!GetExitCodeProcess(_handle, out var exitCode)) return true; // don't false-positive a shutdown on an unrelated error
            const uint STILL_ACTIVE = 259;
            return exitCode == STILL_ACTIVE;
        }
    }

    public void Attach(string processName, bool wait = true, CancellationToken cancellationToken = default)
    {
        var baseName = Path.GetFileNameWithoutExtension(processName);
        while (true)
        {
            var candidates = Process.GetProcessesByName(baseName);
            if (candidates.Length > 0)
            {
                _pid = candidates[0].Id;
                foreach (var p in candidates) p.Dispose();
                _handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, _pid);
                if (_handle == IntPtr.Zero)
                    throw new InvalidOperationException(
                        $"OpenProcess failed for PID {_pid} (win32 error {Marshal.GetLastWin32Error()}) -- run as Administrator?");
                return;
            }
            if (!wait)
                throw new InvalidOperationException($"process '{processName}' not found");
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(1000);
        }
    }

    public void Detach()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public byte[]? ReadBytes(long address, int size)
    {
        if (_handle == IntPtr.Zero || size <= 0) return null;
        var buffer = new byte[size];
        var ok = ReadProcessMemory(_handle, (IntPtr)address, buffer, (IntPtr)size, out var bytesRead);
        if (!ok) return null;
        var read = (int)bytesRead;
        if (read <= 0) return null;
        if (read == size) return buffer;
        var trimmed = new byte[read];
        Array.Copy(buffer, trimmed, read);
        return trimmed;
    }

    public IEnumerable<MemoryRegion> EnumRegions(bool writableOnly = true)
    {
        if (_handle == IntPtr.Zero) yield break;

        long address = 0;
        const long maxAddress = 0x7FFFFFFF0000;
        var mbiSize = (uint)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>();
        var stepCount = 0;

        while (address < maxAddress)
        {
            // See combat_log.py/memlib.py's matching comment (ported from
            // the Python fix): a tight loop of nothing but short,
            // GIL-releasing... well, there's no GIL in C#, but this is
            // cheap insurance against the analogous issue -- a background
            // thread hammering VirtualQueryEx with zero pauses can still
            // starve the UI thread of OS scheduling time on Windows.
            stepCount++;
            if (stepCount % 2000 == 0) Thread.Sleep(1);

            var ret = VirtualQueryEx(_handle, (IntPtr)address, out var mbi, mbiSize);
            if (ret == 0) yield break;

            var regionSize = (long)mbi.RegionSize;
            if (regionSize == 0) yield break;

            var baseAddr = (long)mbi.BaseAddress;
            if (baseAddr == 0) baseAddr = address;

            if (mbi.State == MEM_COMMIT && mbi.Protect != PAGE_NOACCESS && (mbi.Protect & PAGE_GUARD) == 0)
            {
                if (!writableOnly || WritableProtect.Contains(mbi.Protect))
                    yield return new MemoryRegion(baseAddr, regionSize, mbi.Protect);
            }

            address = baseAddr + regionSize;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize; // SIZE_T; IntPtr both matches pointer width and reproduces native struct padding
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
}
