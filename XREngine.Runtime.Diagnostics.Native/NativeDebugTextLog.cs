namespace XREngine.Runtime.Diagnostics.Native;

/// <summary>Writes one native log file with the established sharing and flush policy.</summary>
public sealed class NativeDebugTextLog : IRuntimeDebugTextLog
{
    private readonly StreamWriter _writer;

    private NativeDebugTextLog(StreamWriter writer) => _writer = writer;

    internal static NativeDebugTextLog Open(string filePath)
    {
        FileStream stream = new(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        try
        {
            StreamWriter writer = new(stream) { AutoFlush = true };
            return new NativeDebugTextLog(writer);
        }
        catch
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // Keep the original open error.
            }
            throw;
        }
    }

    public void WriteLine(string? value) => _writer.WriteLine(value);

    public void Dispose() => _writer.Dispose();
}
