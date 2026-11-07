using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WebApi.Infrastructure.Persistence;

internal interface IMigrationEvidenceDurability
{
    Task SyncFileAsync(SafeFileHandle handle, CancellationToken ct);
    Task SyncDirectoryAsync(SafeFileHandle handle, CancellationToken ct);
}

internal sealed class LinuxMigrationEvidenceDurability : IMigrationEvidenceDurability
{
    private static readonly SemaphoreSlim Slots = new(4, 4);
    public Task SyncFileAsync(SafeFileHandle handle, CancellationToken ct) => SyncAsync(handle, ct);
    public Task SyncDirectoryAsync(SafeFileHandle handle, CancellationToken ct) => SyncAsync(handle, ct);

    private static async Task SyncAsync(SafeFileHandle handle, CancellationToken ct)
    {
        if (!await Slots.WaitAsync(TimeSpan.FromSeconds(5), ct)) throw new IOException("migration_evidence_sync_capacity");
        var added = false;
        try { handle.DangerousAddRef(ref added); }
        catch { Slots.Release(); throw; }
        Task<int> worker;
        try
        {
            worker = Task.Run(() =>
            {
                try { return MigrationEvidenceLinux.Sync(handle); }
                finally { if (added) handle.DangerousRelease(); Slots.Release(); }
            });
        }
        catch
        {
            if (added) handle.DangerousRelease();
            Slots.Release();
            throw;
        }
        try
        {
            var error = await worker.WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (error != 0) throw new IOException("migration_evidence_sync_failed: " + error);
        }
        catch (TimeoutException) { throw new IOException("migration_evidence_sync_timeout"); }
    }
}

internal static class MigrationEvidenceLinux
{
    private const int ReadOnly = 0;
    private const int ReadWrite = 2;
    private const int Create = 0x40;
    private const int Exclusive = 0x80;
    private const int CloseOnExec = 0x80000;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasic = 0x7ff;
    private const uint RequiredStatx = 0x30f;
    private const int NotFound = 2;
    private const int AlreadyExists = 17;
    private const int WouldBlock = 11;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;

    // Linux ARM64 defines these two flags differently from x86-64.
    private static int DirectoryFlag => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 0x4000,
        Architecture.X64 => 0x10000,
        _ => throw new InvalidOperationException("migration_evidence_platform_unsupported")
    };
    private static int NoFollow => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => 0x8000,
        Architecture.X64 => 0x20000,
        _ => throw new InvalidOperationException("migration_evidence_platform_unsupported")
    };

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Owner;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatVfsData
    {
        [FieldOffset(8)] public ulong FragmentSize;
        [FieldOffset(32)] public ulong AvailableBlocks;
    }

    internal readonly record struct Identity(uint DeviceMajor, uint DeviceMinor, ulong Inode);
    internal readonly record struct FileInfo(Identity Identity, long Size);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxData data);
    [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)]
    private static extern int Fstatvfs(int fd, out StatVfsData data);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint Geteuid();

    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle().ToInt64());

    internal static SafeFileHandle OpenDirectory(string path)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) throw new InvalidOperationException("migration_evidence_platform_unsupported");
        SafeFileHandle? current = null;
        try
        {
            var root = Open("/", ReadOnly | DirectoryFlag | NoFollow | CloseOnExec, 0);
            if (root < 0) throw UnsafePath();
            current = new SafeFileHandle((IntPtr)root, true);
            foreach (var segment in path.Split('/').Skip(1))
            {
                if (segment is "" or "." or "..") throw UnsafePath();
                var next = OpenAt(Fd(current), segment, ReadOnly | DirectoryFlag | NoFollow | CloseOnExec, 0);
                if (next < 0) throw UnsafePath();
                current.Dispose();
                current = new SafeFileHandle((IntPtr)next, true);
            }
            CheckDirectory(current);
            return current;
        }
        catch { current?.Dispose(); throw; }
    }

    internal static Identity CheckDirectory(SafeFileHandle handle)
    {
        var stat = Stat(handle);
        if ((stat.Mode & 0xf000) != 0x4000 || (stat.Mode & 0x0fff) != 0x1c0 || stat.Owner != Geteuid()) throw UnsafePath();
        return new Identity(stat.DeviceMajor, stat.DeviceMinor, stat.Inode);
    }

    internal static FileInfo CheckFile(SafeFileHandle handle, long maxBytes)
    {
        var stat = Stat(handle);
        if ((stat.Mode & 0xf000) != 0x8000 || (stat.Mode & 0x0fff) != 0x180 || stat.Owner != Geteuid() ||
            stat.Links != 1 || stat.Size > (ulong)maxBytes || stat.Size > long.MaxValue)
            throw new InvalidOperationException("migration_evidence_file_unsafe");
        return new FileInfo(new Identity(stat.DeviceMajor, stat.DeviceMinor, stat.Inode), (long)stat.Size);
    }

    private static StatxData Stat(SafeFileHandle handle)
    {
        if (Statx(Fd(handle), string.Empty, AtEmptyPath, StatxBasic, out var stat) != 0 || (stat.Mask & RequiredStatx) != RequiredStatx)
            throw new InvalidOperationException("migration_evidence_stat_unavailable");
        return stat;
    }

    internal static SafeFileHandle? OpenExisting(SafeFileHandle directory, string name, bool writable = false)
    {
        CheckName(name);
        var fd = OpenAt(Fd(directory), name, (writable ? ReadWrite : ReadOnly) | NoFollow | CloseOnExec, 0);
        if (fd >= 0) return new SafeFileHandle((IntPtr)fd, true);
        if (Marshal.GetLastPInvokeError() == NotFound) return null;
        throw new InvalidOperationException("migration_evidence_file_unsafe");
    }

    internal static SafeFileHandle? CreateNew(SafeFileHandle directory, string name)
    {
        CheckName(name);
        var fd = OpenAt(Fd(directory), name, ReadWrite | Create | Exclusive | NoFollow | CloseOnExec, 0x180);
        if (fd >= 0) return new SafeFileHandle((IntPtr)fd, true);
        if (Marshal.GetLastPInvokeError() == AlreadyExists) return null;
        throw new IOException("migration_evidence_create_failed");
    }

    internal static async Task LockAsync(SafeFileHandle handle, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Flock(Fd(handle), LockExclusive | LockNonBlocking) == 0) return;
            if (Marshal.GetLastPInvokeError() != WouldBlock) throw new IOException("migration_evidence_lock_failed");
            if (started.Elapsed >= TimeSpan.FromSeconds(5)) throw new IOException("migration_evidence_lock_timeout");
            await Task.Delay(25, ct);
        }
    }

    internal static long FreeBytes(SafeFileHandle directory)
    {
        if (Fstatvfs(Fd(directory), out var data) != 0 || data.FragmentSize == 0)
            throw new InvalidOperationException("migration_evidence_free_space_unavailable");
        try { return checked((long)checked(data.FragmentSize * data.AvailableBlocks)); }
        catch (OverflowException) { return long.MaxValue; }
    }

    internal static string AnchoredPath(SafeFileHandle directory) => "/proc/self/fd/" + Fd(directory).ToString(System.Globalization.CultureInfo.InvariantCulture);
    internal static int Sync(SafeFileHandle handle) => Fsync(Fd(handle)) == 0 ? 0 : Marshal.GetLastPInvokeError();

    private static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0')) throw UnsafePath();
    }
    private static InvalidOperationException UnsafePath() => new("migration_evidence_directory_unsafe");
}
