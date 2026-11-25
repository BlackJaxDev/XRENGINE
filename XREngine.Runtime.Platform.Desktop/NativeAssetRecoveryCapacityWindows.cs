using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XREngine.Runtime.Platform.Desktop;

/// <summary>Counts a recovery store through pinned Windows directory handles.</summary>
internal static unsafe class NativeAssetRecoveryCapacityWindows
{
    private const uint ReadAttributes = 0x80;
    private const uint ListDirectory = 0x01;
    private const uint TraverseDirectory = 0x20;
    private const uint ShareAll = 0x07;
    private const uint ShareWithoutDelete = 0x03;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePointWin32 = 0x00200000;
    private const uint OpenReparsePointNt = 0x00200000;
    private const uint DirectoryFile = 0x00000001;
    private const uint NonDirectoryFile = 0x00000040;
    private const uint FileOpen = 1;
    private const uint CaseInsensitive = 0x40;
    private const uint ReparsePoint = 0x400;
    private const uint DirectoryAttribute = 0x10;
    private const int FileStandardInfo = 1;
    private const int FileAttributeTagInfo = 9;
    private const int FileIdInfo = 0x12;
    private const int FileIdExtdDirectoryInfo = 0x13;
    private const int FileIdExtdDirectoryRestartInfo = 0x14;
    private const int NoMoreFiles = 18;
    private const int DirectoryBufferBytes = 64 * 1024;
    private const int EntryHeaderBytes = 88;
    private const int MaximumDirectoryDepth = 64;

    internal static (long Bytes, int Files) Count(string root, long additionalBytes, int additionalFiles,
        long maximumBytes, int maximumFiles)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        string full = Path.GetFullPath(root);
        string volume = Path.GetPathRoot(full) ?? throw new IOException("The recovery store has no volume root.");
        using SafeFileHandle volumeHandle = CreateFileW(volume, ReadAttributes | TraverseDirectory, ShareWithoutDelete,
            0, OpenExisting, BackupSemantics | OpenReparsePointWin32, 0);
        RequireValid(volumeHandle, "The recovery volume root could not be opened.");
        FileIdentity volumeIdentity = RequireDirectory(volumeHandle);

        // Hold each ancestor until the walk ends. No child path is resolved through a string ancestor.
        List<Ancestor> ancestors = [];
        try
        {
            SafeFileHandle parent = volumeHandle;
            string[] names = full[volume.Length..].Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < names.Length; index++)
            {
                string name = names[index];
                SafeFileHandle child = OpenChild(parent, name, isDirectory: true,
                    listDirectory: index == names.Length - 1);
                try
                {
                    FileIdentity identity = RequireDirectory(child);
                    if (identity.Volume != volumeIdentity.Volume)
                        throw new IOException("The recovery store crossed a volume boundary.");
                    ancestors.Add(new Ancestor(child, name, identity));
                    parent = child;
                }
                catch { child.Dispose(); throw; }
            }
            if (ancestors.Count == 0)
                throw new IOException("The recovery store cannot be a volume root.");
            SafeFileHandle store = ancestors[^1].Handle;
            long bytes = 0;
            int files = 0;
            // Reserve every allowed frame slot before a frame allocates its native cursor.
            Stack<DirectoryFrame> pending = new(MaximumDirectoryDepth);
            SafeFileHandle storeParent = ancestors.Count == 1 ? volumeHandle : ancestors[^2].Handle;
            pending.Push(new DirectoryFrame(store, storeParent, ancestors[^1].Name,
                ancestors[^1].Identity, ownsHandle: false));
            try
            {
                while (pending.TryPeek(out DirectoryFrame? frame))
                {
                    if (!frame.Cursor.MoveNext(out DirectoryEntry entry))
                    {
                        if (!RequireDirectory(frame.Handle).Equals(frame.Identity))
                            throw new IOException("A recovery directory changed during capacity inspection.");
                        using (SafeFileHandle reopened = OpenChild(frame.Parent, frame.Name, isDirectory: true))
                            if (!RequireDirectory(reopened).Equals(frame.Identity))
                                throw new IOException("A recovery directory name changed during capacity inspection.");
                        pending.Pop().Dispose();
                        continue;
                    }
                    if (entry.Name is "." or "..") continue;
                    if ((entry.Attributes & ReparsePoint) != 0)
                        throw new IOException("The recovery store contains a linked entry.");
                    bool isDirectory = (entry.Attributes & DirectoryAttribute) != 0;
                    SafeFileHandle child = OpenChild(frame.Handle, entry.Name, isDirectory);
                    bool retained = false;
                    try
                    {
                        FileIdentity current = RequireEntry(child, isDirectory, out long length);
                        if (!current.Equals(new FileIdentity(frame.Identity.Volume, entry.Identity.Id)))
                            throw new IOException("A recovery entry changed during capacity inspection.");
                        if (isDirectory)
                        {
                            if (pending.Count >= MaximumDirectoryDepth)
                                throw new IOException("The recovery store directory nesting is unsupported.");
                            pending.Push(new DirectoryFrame(child, frame.Handle, entry.Name, current, ownsHandle: true));
                            retained = true;
                        }
                        else
                        {
                            bytes = checked(bytes + length);
                            files = checked(files + 1);
                            if (checked(bytes + additionalBytes) > maximumBytes ||
                                checked(files + additionalFiles) > maximumFiles)
                                throw CapacityFull();
                        }
                    }
                    finally { if (!retained) child.Dispose(); }
                }
            }
            finally { while (pending.TryPop(out DirectoryFrame? remaining)) remaining.Dispose(); }
            if (!RequireDirectory(volumeHandle).Equals(volumeIdentity))
                throw new IOException("The recovery volume root changed during capacity inspection.");
            parent = volumeHandle;
            for (int index = 0; index < ancestors.Count; index++)
            {
                Ancestor ancestor = ancestors[index];
                if (!RequireDirectory(ancestor.Handle).Equals(ancestor.Identity))
                    throw new IOException("A recovery ancestor changed during capacity inspection.");
                using (SafeFileHandle reopened = OpenChild(parent, ancestor.Name, isDirectory: true,
                    listDirectory: index == ancestors.Count - 1))
                    if (!RequireDirectory(reopened).Equals(ancestor.Identity))
                        throw new IOException("A recovery ancestor name changed during capacity inspection.");
                parent = ancestor.Handle;
            }
            if (checked(bytes + additionalBytes) > maximumBytes || checked(files + additionalFiles) > maximumFiles)
                throw CapacityFull();
            return (bytes, files);
        }
        finally { foreach (Ancestor ancestor in ancestors) ancestor.Handle.Dispose(); }
    }

    private static IOException CapacityFull()
        => new("Native asset recovery capacity is full. Retained records require explicit review.");

    private sealed class DirectoryFrame : IDisposable
    {
        internal DirectoryFrame(SafeFileHandle handle, SafeFileHandle parent, string name,
            FileIdentity identity, bool ownsHandle)
        {
            Handle = handle;
            Parent = parent;
            Name = name;
            Identity = identity;
            OwnsHandle = ownsHandle;
            Cursor = new DirectoryCursor(handle);
        }
        internal SafeFileHandle Handle { get; }
        internal SafeFileHandle Parent { get; }
        internal string Name { get; }
        internal FileIdentity Identity { get; }
        internal bool OwnsHandle { get; }
        internal DirectoryCursor Cursor { get; }
        public void Dispose()
        {
            Cursor.Dispose();
            if (OwnsHandle) Handle.Dispose();
        }
    }

    private sealed class DirectoryCursor(SafeFileHandle directory) : IDisposable
    {
        private nint _buffer = Marshal.AllocHGlobal(DirectoryBufferBytes);
        private bool _first = true;
        private bool _needBatch = true;
        private int _offset;

        internal bool MoveNext(out DirectoryEntry entry)
        {
            if (_needBatch)
            {
                int kind = _first ? FileIdExtdDirectoryRestartInfo : FileIdExtdDirectoryInfo;
                _first = false;
                if (!GetFileInformationByHandleEx(directory, kind, _buffer, DirectoryBufferBytes))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == NoMoreFiles) { entry = default; return false; }
                    throw new IOException("Recovery directory enumeration failed.", new Win32Exception(error));
                }
                _offset = 0;
                _needBatch = false;
            }
            if (_offset > DirectoryBufferBytes - EntryHeaderBytes)
                throw new InvalidDataException("A recovery directory entry is truncated.");
            uint next = *(uint*)(_buffer + _offset);
            uint attributes = *(uint*)(_buffer + _offset + 56);
            uint nameBytes = *(uint*)(_buffer + _offset + 60);
            if ((nameBytes & 1) != 0 || nameBytes == 0 ||
                nameBytes > DirectoryBufferBytes - EntryHeaderBytes - _offset)
                throw new InvalidDataException("A recovery directory name is invalid.");
            string name = Marshal.PtrToStringUni(_buffer + _offset + EntryHeaderBytes,
                checked((int)nameBytes / 2)) ?? throw new InvalidDataException("A recovery directory name is missing.");
            byte[] id = new byte[16];
            Marshal.Copy(_buffer + _offset + 72, id, 0, id.Length);
            entry = new DirectoryEntry(name, attributes, new FileIdentity(0, id));
            if (next == 0) _needBatch = true;
            else
            {
                if ((next & 7) != 0 || next < EntryHeaderBytes + nameBytes ||
                    next > DirectoryBufferBytes - _offset)
                    throw new InvalidDataException("A recovery directory entry offset is invalid.");
                _offset += checked((int)next);
            }
            return true;
        }

        public void Dispose()
        {
            nint buffer = _buffer;
            _buffer = 0;
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
        }
    }

    private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, bool isDirectory,
        bool listDirectory = true)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0 ||
            name.Length > 255 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new InvalidDataException("A recovery entry has an invalid child name.");
        fixed (char* characters = name)
        {
            UnicodeString unicode = new((ushort)(name.Length * 2), (ushort)(name.Length * 2), characters);
            ObjectAttributes attributes = new((uint)sizeof(ObjectAttributes), parent.DangerousGetHandle(),
                &unicode, CaseInsensitive, null, null);
            // Traverse access keeps an ancestor in sharing checks without list access.
            // Attribute access alone cannot hold the no-delete sharing restriction.
            uint access = ReadAttributes | (isDirectory ? (listDirectory ? ListDirectory : TraverseDirectory) : 0);
            int status = NtCreateFile(out SafeFileHandle child,
                access, &attributes, out _, null, 0,
                isDirectory ? ShareWithoutDelete : ShareAll, FileOpen,
                OpenReparsePointNt | (isDirectory ? DirectoryFile : NonDirectoryFile), null, 0);
            GC.KeepAlive(parent);
            if (status != 0)
            {
                child?.Dispose();
                throw new IOException($"A recovery entry could not be opened (NTSTATUS 0x{status:X8}).");
            }
            RequireValid(child, "A recovery entry returned an invalid handle.");
            return child;
        }
    }

    private static FileIdentity RequireDirectory(SafeFileHandle handle)
    {
        FileIdentity identity = RequireEntry(handle, isDirectory: true, out _);
        return identity;
    }

    private static FileIdentity RequireEntry(SafeFileHandle handle, bool isDirectory, out long length)
    {
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out AttributeTagInfo tag,
                sizeof(AttributeTagInfo)) ||
            !GetFileInformationByHandleEx(handle, FileStandardInfo, out StandardInfo standard,
                sizeof(StandardInfo)) ||
            !GetFileInformationByHandleEx(handle, FileIdInfo, out IdInfo id, sizeof(IdInfo)))
            throw new IOException("Recovery entry metadata could not be read.", new Win32Exception());
        if ((tag.Attributes & ReparsePoint) != 0 || ((tag.Attributes & DirectoryAttribute) != 0) != isDirectory ||
            (standard.Directory != 0) != isDirectory || standard.DeletePending != 0 || standard.EndOfFile < 0)
            throw new IOException("Recovery entry type or attributes changed.");
        length = standard.EndOfFile;
        byte[] fileId = new byte[16];
        for (int index = 0; index < fileId.Length; index++) fileId[index] = id.FileId[index];
        return new FileIdentity(id.VolumeSerial, fileId);
    }

    private static void RequireValid(SafeFileHandle handle, string message)
    {
        if (handle.IsInvalid || handle.IsClosed)
            throw new IOException(message, new Win32Exception());
    }

    private readonly record struct DirectoryEntry(string Name, uint Attributes, FileIdentity Identity);
    private readonly record struct Ancestor(SafeFileHandle Handle, string Name, FileIdentity Identity);
    private readonly struct FileIdentity(ulong volume, byte[] id)
    {
        public ulong Volume { get; } = volume;
        public byte[] Id { get; } = id;
        public bool Equals(FileIdentity other) => Volume == other.Volume && Id.AsSpan().SequenceEqual(other.Id);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString(ushort length, ushort maximumLength, char* buffer)
    {
        public readonly ushort Length = length;
        public readonly ushort MaximumLength = maximumLength;
        public readonly char* Buffer = buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ObjectAttributes(uint length, nint rootDirectory, UnicodeString* objectName,
        uint attributes, void* securityDescriptor, void* securityQualityOfService)
    {
        public readonly uint Length = length;
        public readonly nint RootDirectory = rootDirectory;
        public readonly UnicodeString* ObjectName = objectName;
        public readonly uint Attributes = attributes;
        public readonly void* SecurityDescriptor = securityDescriptor;
        public readonly void* SecurityQualityOfService = securityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public nint Status; public nint Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdInfo { public ulong VolumeSerial; public fixed byte FileId[16]; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        nint buffer, uint bufferSize);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out AttributeTagInfo buffer, int bufferSize);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out StandardInfo buffer, int bufferSize);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out IdInfo buffer, int bufferSize);
    [DllImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static extern int NtCreateFile(out SafeFileHandle file, uint desiredAccess,
        ObjectAttributes* objectAttributes, out IoStatusBlock ioStatusBlock, void* allocationSize,
        uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions,
        void* eaBuffer, uint eaLength);
}
