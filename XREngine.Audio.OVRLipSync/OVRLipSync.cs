using System.Runtime.InteropServices;

namespace XREngine.Components
{
    /// <summary>
    /// Custom C# implementation of Meta's OVRLipSync for facial animation using audio input.
    /// https://developers.meta.com/horizon/licenses/oculussdk/
    /// </summary>
    public static class OVRLipSync
    {
        public enum Viseme
        {
            sil,
            PP,
            FF,
            TH,
            DD,
            kk,
            CH,
            SS,
            nn,
            RR,
            aa,
            E,
            ih,
            oh,
            ou
        };

        public static readonly string[] VisemeNames = Enum.GetNames<Viseme>();
        public static readonly int VisemeCount = VisemeNames.Length;

        /// OVRLipSync library major version
        public const int OVR_LIPSYNC_MAJOR_VERSION = 1;
        /// OVRLipSync library minor version
        public const int OVR_LIPSYNC_MINOR_VERSION = 61;
        /// OVRLipSync library patch version
        public const int OVR_LIPSYNC_PATCH_VERSION = 0;

        /// Result type used by the OVRLipSync API
        /// Success is zero, while all error types are non-zero values.
        public enum ovrLipSyncResult
        {
            ovrLipSyncSuccess = 0,
            // ERRORS
            /// An unknown error has occurred
            ovrLipSyncError_Unknown = -2200,
            /// Unable to create a context
            ovrLipSyncError_CannotCreateContext = -2201,
            /// An invalid parameter, e.g. NULL pointer or out of range
            ovrLipSyncError_InvalidParam = -2202,
            /// An unsupported sample rate was declared
            ovrLipSyncError_BadSampleRate = -2203,
            /// The DLL or shared library could not be found
            ovrLipSyncError_MissingDLL = -2204,
            /// Mismatched versions between header and libs
            ovrLipSyncError_BadVersion = -2205,
            /// An undefined function
            ovrLipSyncError_UndefinedFunction = -2206
        }

        /// Audio buffer data type
        public enum ovrLipSyncAudioDataType
        {
            /// Signed 16-bit integer mono audio stream
            ovrLipSyncAudioDataType_S16_Mono,
            /// Signed 16-bit integer stereo audio stream
            ovrLipSyncAudioDataType_S16_Stereo,
            /// Signed 32-bit float mono data type
            ovrLipSyncAudioDataType_F32_Mono,
            /// Signed 32-bit float stereo data type
            ovrLipSyncAudioDataType_F32_Stereo,
        }

        /// Visemes
        ///
        /// \see struct ovrLipSyncFrame
        public enum ovrLipSyncViseme
        {
            /// Silent viseme
            ovrLipSyncViseme_sil,
            /// PP viseme (corresponds to p,b,m phonemes in worlds like \a put , \a bat, \a mat)
            ovrLipSyncViseme_PP,
            /// FF viseme (corrseponds to f,v phonemes in the worlds like \a fat, \a vat)
            ovrLipSyncViseme_FF,
            /// TH viseme (corresponds to th phoneme in words like \a think, \a that)
            ovrLipSyncViseme_TH,
            /// DD viseme (corresponds to t,d phonemes in words like \a tip or \a doll)
            ovrLipSyncViseme_DD,
            /// kk viseme (corresponds to k,g phonemes in words like \a call or \a gas)
            ovrLipSyncViseme_kk,
            /// CH viseme (corresponds to tS,dZ,S phonemes in words like \a chair, \a join, \a she)
            ovrLipSyncViseme_CH,
            /// SS viseme (corresponds to s,z phonemes in words like \a sir or \a zeal)
            ovrLipSyncViseme_SS,
            /// nn viseme (corresponds to n,l phonemes in worlds like \a lot or \a not)
            ovrLipSyncViseme_nn,
            /// RR viseme (corresponds to r phoneme in worlds like \a red)
            ovrLipSyncViseme_RR,
            /// aa viseme (corresponds to A: phoneme in worlds like \a car)
            ovrLipSyncViseme_aa,
            /// E viseme (corresponds to e phoneme in worlds like \a bed)
            ovrLipSyncViseme_E,
            /// I viseme (corresponds to ih phoneme in worlds like \a tip)
            ovrLipSyncViseme_ih,
            /// O viseme (corresponds to oh phoneme in worlds like \a toe)
            ovrLipSyncViseme_oh,
            /// U viseme (corresponds to ou phoneme in worlds like \a book)
            ovrLipSyncViseme_ou,

            /// Total number of visemes
            ovrLipSyncViseme_Count
        }

        /// Laughter types
        ///
        /// \see struct ovrLipSyncFrame
        public enum ovrLipSyncLaughterCategory
        {
            ovrLipSyncLaughterCategory_Count
        }

        /// Supported signals to send to LipSync API
        ///
        /// \see ovrLipSync_SendSignal
        public enum ovrLipSyncSignals
        {
            ovrLipSyncSignals_VisemeOn, ///< fully on  (1.0f)
            ovrLipSyncSignals_VisemeOff, ///< fully off (0.0f)
            ovrLipSyncSignals_VisemeAmount, ///< Set to a given amount (0 - 100)
            ovrLipSyncSignals_VisemeSmoothing, ///< Amount to set per frame to target (0 - 100)
            ovrLipSyncSignals_LaughterAmount, ///< Set to a given amount (0.0-1.0)
            ovrLipSyncSignals_Count
        }

        /// Context provider
        ///
        /// \see ovrLipSync_CreateContext
        public enum ovrLipSyncContextProvider
        {
            ovrLipSyncContextProvider_Original,
            ovrLipSyncContextProvider_Enhanced,
            ovrLipSyncContextProvider_EnhancedWithLaughter
        }

        /// Current lipsync frame results
        ///
        /// \see ovrLipSync_ProcessFrame
        /// \see ovrLipSync_ProcessFrameInterleaved
        [StructLayout(LayoutKind.Sequential)]
        public struct ovrLipSyncFrame
        {
            public int frameNumber; ///< count from start of recognition
            public int frameDelay; ///< Frame delay in milliseconds
            public IntPtr visemes; ///< Pointer to Viseme array, sizeof ovrLipSyncViseme_Count
            public uint visemesLength; ///< Number of visemes in array

            /// Laughter score for the current audio frame
            public float laughterScore;
            /// Per-laughter category score, sizeof ovrLipSyncLaughterCategory_Count
            public IntPtr laughterCategories;
            /// Number of laughter categories
            public uint laughterCategoriesLength; ///< Number of laughter categories
        }

        /// Opaque type def for LipSync context
        public struct ovrLipSyncContext
        {
            public UInt32 handle;
        }

        /// Callback function type
        /// \param[in] opaque an opaque pointer passed to the callback
        /// \param[in] pFrame pointer to a frame predicted by asynchronous operation (or nullptr if error
        /// occured) \param[in] result Result of asyncrhonous operation \see ovrLipSync_ProcessFrameAsync
        public delegate void ovrLipSyncCallback(IntPtr opaque, IntPtr pFrame, ovrLipSyncResult result);


        /// Initialize OVRLipSync
        ///
        /// Load the OVR LipSync library.  Call this first before any other ovrLipSync
        /// functions!
        ///
        /// \param[in] sampleRate Default sample rate for all created context
        /// \param[in] bufferSize Default buffer size of all context
        ///
        /// \return Returns an ovrLipSyncResult indicating success or failure.
        ///
        /// \see ovrLipSync_Shutdown
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_Initialize(int sampleRate, int bufferSize);

        /// Initialize OVRLipSyncEx
        ///
        /// Load the OVR LipSync library.  Call this first before any other ovrLipSync
        /// functions!
        ///
        /// \param[in] sampleRate Default sample rate for all created context
        /// \param[in] bufferSize Default buffer size of all context
        /// \param[in] path Path to the folder where OVR LipSync library is located.
        ///
        /// \return Returns an ovrLipSyncResult indicating success or failure.
        ///
        /// \see ovrLipSync_Shutdown
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_InitializeEx(int sampleRate, int bufferSize, string path);

        /// Shutdown OVRLipSync
        ///
        ///  Shut down all of ovrLipSync.
        ///
        ///  \see ovrLipSync_Initialize
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_Shutdown();

        /// Return library's built version information.
        ///
        /// Can be called any time.
        /// \param[out] major Pointer to integer that accepts major version number
        /// \param[out] minor Pointer to integer that accepts minor version number
        /// \param[out] patch Pointer to integer that accepts patch version number
        ///
        /// \return Returns a string with human readable build information
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern string ovrLipSyncDll_GetVersion(out int major, out int minor, out int patch);

        /// Create a lip-sync context for transforming incoming audio
        /// into a stream of visemes.
        ///
        /// \param[out] pContext pointer to store address of context.
        ///             NOTE: pointer must be pointing to NULL!
        /// \param[in] provider sets the desired provider to use
        /// \return Returns an ovrLipSyncResult indicating success or failure
        /// \see ovrLipSync_DeleteContext
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_CreateContext(ref ovrLipSyncContext pContext, ovrLipSyncContextProvider provider);

        /// Create a lip-sync context for transforming incoming audio
        /// into a stream of visemes.
        ///
        /// \param[out] pContext pointer to store address of context.
        ///             NOTE: pointer must be pointing to NULL!
        /// \param[in] provider sets the desired provider to use
        /// \param[in] sampleRate sample rate of the audio stream
        ///            NOTE: Pass zero to use default sample rate
        /// \param[in] enableAcceleration Specifies whether to allow HW acceleration on supported platforms
        /// \return Returns an ovrLipSyncResult indicating success or failure
        /// \see ovrLipSync_DeleteContext
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_CreateContextEx(ref ovrLipSyncContext pContext, ovrLipSyncContextProvider provider, int sampleRate, bool enableAcceleration);

        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_CreateContextWithModelFile(ref ovrLipSyncContext context, ovrLipSyncContextProvider provider, string modelPath, int sampleRate, bool enableAcceleration);

        /// Destroy a previously created lip-sync context.
        ///
        /// \param[in] context A valid lip-sync context
        /// \see ovrLipSync_CreateContext
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_DestroyContext(ovrLipSyncContext context);

        /// Reset the internal state of a lip-sync context.
        ///
        /// \param[in] context A valid lip-sync context
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_ResetContext(ovrLipSyncContext context);

        /// Send context various signals to drive its output state.
        ///
        /// \param[in] context a valid lip-sync context
        /// \param[in] signal signal type to send to context
        /// \param[in] arg1 first argument based on signal type
        /// \param[in] arg2 second argument based on signal type
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_SendSignal(ovrLipSyncContext context, ovrLipSyncSignals signal, int arg1, int arg2);

        /// Send context a mono audio buffer and receive a frame of viseme data
        ///
        /// \param[in] context A valid lip-sync context
        /// \param[in] audioBuffer A pointer to an audio buffer (mono)
        /// \param[out] pFrame A pointer to the current viseme frame
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_ProcessFrame(ovrLipSyncContext context, float[] audioBuffer, ref ovrLipSyncFrame pFrame);

        /// Send context an interleaved (stereo) audio buffer and receive a frame of viseme data
        ///
        /// \param[in] context A valid lip-sync context
        /// \param[in] audioBuffer A pointer to an audio buffer (stereo)
        /// \param[out] pFrame A pointer to the current viseme frame
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_ProcessFrameInterleaved(ovrLipSyncContext context, float[] audioBuffer, ref ovrLipSyncFrame pFrame);

        /// Send context an audio buffer(mono or stereo) and receive a frame of viseme data
        ///
        /// \param[in] context A valid lip-sync context
        /// \param[in] audioBuffer A pointer to an audio buffer
        /// \param[in] sampleCount Size of audioBuffer in number of samples
        /// \param[in] dataType Audio buffer data type: 32-bit float or 16-bit integer mono or stereo stream
        /// \param[out] pFrame A pointer to the current viseme frame
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_ProcessFrameEx(uint context,
            IntPtr audioBuffer,
            uint bufferSize,
            ovrLipSyncAudioDataType dataType,
            ref int frameNumber,
            ref int frameDelay,
            float[] visemes,
            int visemeCount,
            ref float laughterScore,
            float[]? laughterCategories,
            int laughterCategoriesLength);

        /// Send context an audio buffer(mono or stereo) and receive a notification when processing is done
        ///
        /// \param[in] context A valid lip-sync context
        /// \param[in] audioBuffer A pointer to an audio buffer
        /// \param[in] sampleCount Size of audioBuffer in number of samples
        /// \param[in] dataType Audio buffer data type: 32-bit float or 16-bit integer mono or stereo stream
        /// \param[in] callback Pointer to a callback function
        /// \param[in] opaque Value to be passed as first argument when callback is invoked
        ///
        [DllImport("OVRLipSync", CallingConvention = CallingConvention.Cdecl)]
        public static extern ovrLipSyncResult ovrLipSyncDll_ProcessFrameAsync(ovrLipSyncContext context, IntPtr audioBuffer, int sampleCount, ovrLipSyncAudioDataType dataType, ovrLipSyncCallback callback, IntPtr opaque);
    }
}
