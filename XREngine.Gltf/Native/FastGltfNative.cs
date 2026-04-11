using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace XREngine.Gltf;

public enum FastGltfCopyFormat : int
{
    Float32Scalar = 0,
    Float32Vec2 = 1,
    Float32Vec3 = 2,
    Float32Vec4 = 3,
    Float32Mat4 = 4,
    UInt32Scalar = 5,
    UInt32Vec4 = 6,
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct UInt4(uint x, uint y, uint z, uint w)
{
    public uint X { get; } = x;
    public uint Y { get; } = y;
    public uint Z { get; } = z;
    public uint W { get; } = w;
}

internal enum FastGltfStatus : int
{
    Success = 0,
    InvalidArgument = 1,
    ParseFailed = 2,
    NotFound = 3,
    BufferTooSmall = 4,
    CopyFailed = 5,
}

internal sealed class FastGltfNativeAssetHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private FastGltfNativeAssetHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        FastGltfNative.CloseAsset(handle);
        return true;
    }
}

internal static class FastGltfNative
{
    private const string LibraryName = "FastGltfBridge.Native";

    static FastGltfNative()
        => NativeLibrary.SetDllImportResolver(typeof(FastGltfNative).Assembly, ResolveLibrary);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern FastGltfStatus xre_fastgltf_open_asset_utf8(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out FastGltfNativeAssetHandle handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void xre_fastgltf_close_asset(nint handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern FastGltfStatus xre_fastgltf_copy_last_error_utf8(
        FastGltfNativeAssetHandle handle,
        nint buffer,
        nuint bufferLength,
        out nuint writtenLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern FastGltfStatus xre_fastgltf_get_buffer_view_byte_length(
        FastGltfNativeAssetHandle handle,
        uint bufferViewIndex,
        out nuint byteLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe FastGltfStatus xre_fastgltf_copy_buffer_view_bytes(
        FastGltfNativeAssetHandle handle,
        uint bufferViewIndex,
        void* destination,
        nuint destinationLength);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern unsafe FastGltfStatus xre_fastgltf_copy_accessor(
        FastGltfNativeAssetHandle handle,
        uint accessorIndex,
        FastGltfCopyFormat format,
        void* destination,
        nuint destinationLength,
        nuint destinationStride);

    public static FastGltfNativeAssetHandle OpenAsset(string path)
    {
        FastGltfStatus status = xre_fastgltf_open_asset_utf8(path, out FastGltfNativeAssetHandle handle);
        if (status == FastGltfStatus.Success && !handle.IsInvalid)
            return handle;

        string details = string.Empty;
        if (handle is not null && !handle.IsInvalid)
            details = ReadLastError(handle);

        handle?.Dispose();
        throw new InvalidOperationException($"Failed to open glTF asset '{path}': {status}. {details}".Trim());
    }

    public static unsafe void CopyAccessor<T>(FastGltfNativeAssetHandle handle, int accessorIndex, FastGltfCopyFormat format, T[] destination, nuint stride = 0)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(destination);

        fixed (T* destinationPtr = destination)
        {
            nuint destinationLength = checked((nuint)(destination.Length * Unsafe.SizeOf<T>()));
            FastGltfStatus status = xre_fastgltf_copy_accessor(handle, checked((uint)accessorIndex), format, destinationPtr, destinationLength, stride);
            EnsureSuccess(handle, status, $"copy accessor {accessorIndex}");
        }
    }

    public static unsafe byte[] CopyBufferView(FastGltfNativeAssetHandle handle, int bufferViewIndex)
    {
        ArgumentNullException.ThrowIfNull(handle);

        FastGltfStatus lengthStatus = xre_fastgltf_get_buffer_view_byte_length(handle, checked((uint)bufferViewIndex), out nuint byteLength);
        EnsureSuccess(handle, lengthStatus, $"get buffer view length {bufferViewIndex}");

        byte[] bytes = new byte[checked((int)byteLength)];
        fixed (byte* destinationPtr = bytes)
        {
            FastGltfStatus copyStatus = xre_fastgltf_copy_buffer_view_bytes(handle, checked((uint)bufferViewIndex), destinationPtr, byteLength);
            EnsureSuccess(handle, copyStatus, $"copy buffer view {bufferViewIndex}");
        }

        return bytes;
    }

    internal static void CloseAsset(nint handle)
        => xre_fastgltf_close_asset(handle);

    private static void EnsureSuccess(FastGltfNativeAssetHandle handle, FastGltfStatus status, string operation)
    {
        if (status == FastGltfStatus.Success)
            return;

        throw new InvalidOperationException($"FastGltfBridge failed to {operation}: {status}. {ReadLastError(handle)}".Trim());
    }

    private static unsafe string ReadLastError(FastGltfNativeAssetHandle handle)
    {
        const int bufferSize = 4096;
        byte[] buffer = new byte[bufferSize];
        fixed (byte* bufferPtr = buffer)
        {
            FastGltfStatus status = xre_fastgltf_copy_last_error_utf8(handle, (nint)bufferPtr, (nuint)buffer.Length, out nuint writtenLength);
            if (status != FastGltfStatus.Success || writtenLength == 0)
                return string.Empty;

            int length = checked((int)Math.Min((nuint)buffer.Length, writtenLength));
            return System.Text.Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\0');
        }
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        foreach (string candidate in EnumerateCandidatePaths(assembly))
        {
            if (!File.Exists(candidate))
                continue;

            return NativeLibrary.Load(candidate, assembly, searchPath);
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> EnumerateCandidatePaths(Assembly assembly)
    {
        string fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"{LibraryName}.dll"
            : LibraryName;
        string runtimeRelativePath = Path.Combine("runtimes", "win-x64", "native", fileName);

        yield return Path.Combine(AppContext.BaseDirectory, fileName);
        yield return Path.Combine(AppContext.BaseDirectory, runtimeRelativePath);

        string assemblyDirectory = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        yield return Path.Combine(assemblyDirectory, fileName);
        yield return Path.Combine(assemblyDirectory, runtimeRelativePath);

        DirectoryInfo? current = new(assemblyDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "XREngine.Gltf", runtimeRelativePath);
            current = current.Parent;
        }
    }
}