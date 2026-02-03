using System.Diagnostics;
using SysTrace = System.Diagnostics.Trace;

namespace XREngine
{
    /// <summary>
    /// A trace listener that bridges System.Diagnostics.Debug/Trace output to the engine's logging system.
    /// Install via <see cref="InstallGlobalListener"/> to capture all Debug.WriteLine calls from external libraries.
    /// </summary>
    public class TraceListener : ConsoleTraceListener
    {
        private bool _isActive;
        private readonly object _lock = new();

        /// <summary>
        /// Fired when a message is received. Used by legacy ConsolePanel UI component.
        /// </summary>
        public event TraceListenerEventHandler? TraceListenerEvent;
        public delegate void TraceListenerEventHandler(string? message);

        /// <summary>
        /// Callback invoked for each message received. Set this to route messages to the engine's Debug.Log system.
        /// </summary>
        public static Action<string?>? GlobalMessageCallback { get; set; }

        private static TraceListener? _globalInstance;
        private static readonly object _installLock = new();

        /// <summary>
        /// Installs a global TraceListener that captures all System.Diagnostics.Debug and Trace output.
        /// Safe to call multiple times - only installs once.
        /// </summary>
        public static void InstallGlobalListener()
        {
            lock (_installLock)
            {
                if (_globalInstance is not null)
                    return;

                _globalInstance = new TraceListener();
                // Debug and Trace share the same Listeners collection via Trace.Listeners
                SysTrace.Listeners.Add(_globalInstance);
            }
        }

        //TODO: output to session file using stream
        public override void WriteLine(string? message)
            => Write(message + Environment.NewLine);

        public override void Write(string? message)
        {
            // Avoid possibility of stack overflow from re-entrant logging
            // Use lock to prevent multiple threads from writing simultaneously
            lock (_lock)
            {
                if (_isActive)
                    return;

                try
                {
                    _isActive = true;
                    
                    // Route to engine's logging system if callback is set
                    GlobalMessageCallback?.Invoke(message);
                    
                    // Also fire legacy event for ConsolePanel
                    TraceListenerEvent?.Invoke(message);
                    
                    // Write to console (but not to base which would cause recursion with Debug output)
                    Console.Write(message);
                }
                finally
                {
                    _isActive = false;
                }
            }
        }
    }
}
