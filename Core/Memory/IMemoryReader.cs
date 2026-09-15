namespace MnmDamageParser.Core.Memory;

public readonly record struct MemoryRegion(long Base, long Size, uint Protect);

/// <summary>Everything the heap scanner needs from the target process.
/// Split out as an interface (mirroring memlib.py's MemoryHandle) so the
/// scanning/parsing logic can be unit-tested with a fake implementation
/// with no Windows dependency at all -- only Win32MemoryReader actually
/// needs Windows.</summary>
public interface IMemoryReader
{
    bool IsAlive { get; }
    void Attach(string processName, bool wait = true, CancellationToken cancellationToken = default);
    void Detach();

    /// <summary>Null on any read failure (partial reads, access errors,
    /// the process exiting mid-read, etc.) -- callers treat null as "skip
    /// this region", same as the Python version's try/except-wrapped
    /// read_bytes.</summary>
    byte[]? ReadBytes(long address, int size);

    IEnumerable<MemoryRegion> EnumRegions(bool writableOnly = true);
}
