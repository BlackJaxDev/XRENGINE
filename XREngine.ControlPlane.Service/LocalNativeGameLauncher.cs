using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace XREngine.ControlPlane.Service;

/// <summary>
/// Starts an allowlisted local game with a short-lived, current-user-only managed launch configuration.
/// Browser callers never receive the configuration or its player admission secret.
/// </summary>
internal sealed class LocalNativeGameLauncher(LocalServiceOptions options, ILogger<LocalNativeGameLauncher> logger)
{
    private const string ManagedClientConfigFile = "XRE_MANAGED_CLIENT_CONFIG_FILE";
    private const string DeleteConfigAfterRead = "XRE_MANAGED_CLIENT_CONFIG_DELETE_AFTER_READ";

    public bool IsEnabled => options.NativeLauncher.Enabled;

    /// <summary>
    /// Launches a local native game with the specified managed client launch configuration.
    /// </summary>
    /// <param name="launch">The managed client launch configuration to use for starting the game.</param>
    /// <returns>The process ID of the started native game process.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the native game process could not be started or if native launch is not enabled.</exception>
    public int Launch(ManagedClientLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        if (!IsEnabled)
            throw new InvalidOperationException("Native launch is not enabled by this local service configuration.");

        string handoffDirectory = CreateRestrictedHandoffDirectory();
        string handoffPath = Path.Combine(handoffDirectory, Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + ".json");
        try
        {
            using (System.IO.FileStream stream = new(handoffPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                JsonSerializer.Serialize(stream, launch, XreControlPlaneJsonContext.Default.ManagedClientLaunch);

            ProcessStartInfo start = new(options.NativeLauncher.GameExecutable, options.NativeLauncher.Arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(options.NativeLauncher.GameExecutable)!,
            };
            start.Environment[ManagedClientConfigFile] = handoffPath;
            start.Environment[DeleteConfigAfterRead] = "1";
            Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The configured native game process did not start.");
            
            logger.LogInformation("Started local native managed client process {ProcessId}.", process.Id);
            return process.Id;
        }
        catch
        {
            TryDelete(handoffPath);
            throw;
        }
    }

    /// <summary>
    /// Creates a restricted directory for handing off configuration files to the native game process, ensuring that only the current Windows user has access.
    /// </summary>
    /// <returns>The path to the created restricted handoff directory.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if the operating system is not Windows, as the local native launcher requires Windows file ACLs.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the current Windows user cannot be identified or if the restricted handoff directory cannot be created.</exception>
    private string CreateRestrictedHandoffDirectory()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The local native launcher currently requires Windows file ACLs.");

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The local native launcher could not identify the current Windows user.");
        string path = Path.Combine(options.WorkingRoot, "native-handoffs");
        Directory.CreateDirectory(path);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user, 
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, 
            PropagationFlags.None, 
            AccessControlType.Allow));
        
        new DirectoryInfo(path).SetAccessControl(security);

        return path;
    }

    /// <summary>
    /// Attempts to delete the specified file, ignoring any IO or unauthorized access exceptions.
    /// </summary>
    /// <param name="path">The path to the file to be deleted.</param>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
