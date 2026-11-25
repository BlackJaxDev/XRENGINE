using System.Diagnostics;

namespace XREngine.Editor.Services;

/// <summary>Reveals an editor path in the Windows file browser.</summary>
internal static class EditorFileBrowser
{
    public static void OpenPath(string path, bool isDirectory)
    {
        try
        {
            string arguments = isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, $"Failed to open explorer for '{path}'.");
        }
    }
}
