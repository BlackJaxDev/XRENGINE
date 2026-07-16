namespace XREngine.Rendering
{
public static partial class GpuDispatchLogger
    {
        /// <summary>
        /// Log verbosity levels.
        /// </summary>
        public enum LogLevel
        {
            /// <summary>Only critical errors</summary>
            Error = 0,
            /// <summary>Warnings and errors</summary>
            Warning = 1,
            /// <summary>Informational messages</summary>
            Info = 2,
            /// <summary>Detailed debug information</summary>
            Debug = 3,
            /// <summary>Extremely verbose trace logging</summary>
            Trace = 4
        }
    }
}
