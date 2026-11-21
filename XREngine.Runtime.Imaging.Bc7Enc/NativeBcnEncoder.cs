using System.Runtime.InteropServices;

namespace XREngine.Runtime.Imaging.Bc7Enc;

/// <summary>
/// P/Invoke surface of the native xre_bcn shim, which wraps the bc7enc_rdo encoders.
/// Tools/Dependencies/Build-Bc7Enc.ps1 builds the library into Build/Dependencies/Bc7Enc/&lt;rid&gt;/.
/// </summary>
internal static partial class NativeBcnEncoder
{
    private const string NativeLibraryName = "xre_bcn";
    private const string VersionFileName = "xre_bcn.version";

    /// <summary>The C ABI version that this wrapper expects from xre_bcn_api_version.</summary>
    internal const int ExpectedApiVersion = 1;

    internal const int ResultOk = 0;
    internal const int ResultInvalidArgument = -1;
    internal const int ResultOutputTooSmall = -2;
    internal const int ResultNotInitialized = -3;

    private static readonly Lazy<nint> s_nativeLibraryHandle = new(LoadNativeLibraryHandle, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> s_versionText = new(ReadVersionText, LazyThreadSafetyMode.ExecutionAndPublication);

    static NativeBcnEncoder()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeBcnEncoder).Assembly, ResolveNativeLibrary);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already set for this assembly.
        }
    }

    /// <summary>The contents of xre_bcn.version, or an empty string when the file is missing.</summary>
    internal static string VersionText => s_versionText.Value;

    private static string CurrentRid => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    private static string NativeFileName => OperatingSystem.IsWindows() ? "xre_bcn.dll" : "libxre_bcn.so";

    private static nint ResolveNativeLibrary(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        => libraryName == NativeLibraryName ? s_nativeLibraryHandle.Value : IntPtr.Zero;

    private static IEnumerable<string> EnumerateProbeDirectories()
    {
        string? assemblyDirectory = Path.GetDirectoryName(typeof(NativeBcnEncoder).Assembly.Location);
        string baseDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(assemblyDirectory))
            yield return assemblyDirectory;

        yield return baseDirectory;

        if (!string.IsNullOrEmpty(assemblyDirectory))
            yield return Path.Combine(assemblyDirectory, "runtimes", CurrentRid, "native");

        yield return Path.Combine(baseDirectory, "runtimes", CurrentRid, "native");
    }

    private static nint LoadNativeLibraryHandle()
    {
        foreach (string directory in EnumerateProbeDirectories())
        {
            string path = Path.Combine(directory, NativeFileName);
            if (File.Exists(path))
                return NativeLibrary.Load(path);
        }

        throw new DllNotFoundException(
            $"The native block-compression library '{NativeFileName}' was not found. " +
            $"Run 'pwsh Tools/Dependencies/Build-Bc7Enc.ps1 -Rid {CurrentRid}' one time, and then build again.");
    }

    private static string ReadVersionText()
    {
        foreach (string directory in EnumerateProbeDirectories())
        {
            string path = Path.Combine(directory, VersionFileName);
            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }

        return string.Empty;
    }

    [LibraryImport(NativeLibraryName, EntryPoint = "xre_bcn_api_version")]
    internal static partial int ApiVersion();

    [LibraryImport(NativeLibraryName, EntryPoint = "xre_bcn_init")]
    internal static partial void Initialize();

    [LibraryImport(NativeLibraryName, EntryPoint = "xre_bcn_encode_bc7")]
    internal static unsafe partial int EncodeBc7(byte* rgba, uint width, uint height, uint uberLevel, int perceptual, byte* output, nuint outputSize);

    [LibraryImport(NativeLibraryName, EntryPoint = "xre_bcn_encode_bc4")]
    internal static unsafe partial int EncodeBc4(byte* rgba, uint width, uint height, uint channel, byte* output, nuint outputSize);

    [LibraryImport(NativeLibraryName, EntryPoint = "xre_bcn_encode_bc5")]
    internal static unsafe partial int EncodeBc5(byte* rgba, uint width, uint height, uint channel0, uint channel1, byte* output, nuint outputSize);

    /// <summary>Returns a description of a native result code.</summary>
    internal static string DescribeResult(int result)
        => result switch
        {
            ResultOk => "success",
            ResultInvalidArgument => "an argument was not valid",
            ResultOutputTooSmall => "the output buffer was too small",
            ResultNotInitialized => "the encoder was not initialized",
            _ => $"unknown result {result}",
        };
}
