using Silk.NET.OpenAL;
using System.Diagnostics;

namespace XREngine.Audio
{
    /// <summary>
    /// Loads the OpenAL Soft library that the Silk.NET.OpenAL.Soft.Native package ships next to the application.
    /// <para>
    /// The OpenAL transport uses only this library. It does not load the system OpenAL router
    /// (<c>OpenAL32.dll</c> on Windows), because the router does not supply <c>AL_SOFT_source_latency</c>.
    /// Without that extension, the cutscene audio clock cannot read the device position.
    /// </para>
    /// <para>
    /// OpenAL Soft is LGPL. The library stays a separate file that the process loads at run time.
    /// Do not link it statically.
    /// </para>
    /// </summary>
    internal static class OpenALSoftLibrary
    {
        /// <summary>The file name of the OpenAL Soft library on Windows.</summary>
        public const string WindowsFileName = "soft_oal.dll";

        /// <summary>Loads the AL entry points from OpenAL Soft.</summary>
        /// <exception cref="InvalidOperationException">OpenAL Soft is not available.</exception>
        public static AL LoadAl()
        {
            try
            {
                return AL.GetApi(soft: true);
            }
            catch (Exception error)
            {
                throw CreateLoadFailure("AL", error);
            }
        }

        /// <summary>Loads the ALC entry points from OpenAL Soft.</summary>
        /// <exception cref="InvalidOperationException">OpenAL Soft is not available.</exception>
        public static ALContext LoadAlc()
        {
            try
            {
                return ALContext.GetApi(soft: true);
            }
            catch (Exception error)
            {
                throw CreateLoadFailure("ALC", error);
            }
        }

        private static InvalidOperationException CreateLoadFailure(string api, Exception error)
        {
            string message =
                $"OpenAL could not load the OpenAL Soft {api} entry points ({WindowsFileName} on Windows). " +
                "The OpenAL transport does not use the system OpenAL router in its place, because the router " +
                "has no AL_SOFT_source_latency and the audio clock would change without notice. " +
                "Make sure that the Silk.NET.OpenAL.Soft.Native output is next to the application. " +
                $"Cause: {error.GetType().Name}: {error.Message}";
            Trace.WriteLine(message);
            AudioDiagnostics.RecordOpenALError(message);
            return new InvalidOperationException(message, error);
        }
    }
}
