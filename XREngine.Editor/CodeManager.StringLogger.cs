using Microsoft.Build.Framework;
using System.Text;

internal partial class CodeManager
{
    // Custom logger that captures all output to a string
    private class StringLogger(LoggerVerbosity verbosity) : ILogger
    {
        private readonly StringBuilder _log = new();

        public void Initialize(IEventSource eventSource)
        {
            eventSource.ErrorRaised += (sender, e) =>
                _log.AppendLine($"ERROR {e.Code}: {e.Message} ({e.File}:{e.LineNumber},{e.ColumnNumber})");

            eventSource.WarningRaised += (sender, e) =>
                _log.AppendLine($"WARNING {e.Code}: {e.Message} ({e.File}:{e.LineNumber},{e.ColumnNumber})");

            eventSource.MessageRaised += (sender, e) =>
                _log.AppendLine($"{e.Message}");
        }

        public string GetFullLog() => _log.ToString();

        public void Shutdown() { }

        public LoggerVerbosity Verbosity
        {
            get => verbosity;
            set => verbosity = value;
        }

        private string? _parameters = null;
        public string? Parameters
        {
            get => _parameters;
            set => _parameters = value;
        }
    }
}
