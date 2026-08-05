using System.Runtime.InteropServices;

namespace XREngine.Components
{
    public static partial class Audio2Face3DNativeBridge
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSessionConfig
        {
            public int InputSampleRate;

            [MarshalAs(UnmanagedType.I1)]
            public bool EnableEmotion;

            [MarshalAs(UnmanagedType.LPUTF8Str)]
            public string? FaceModelPath;

            [MarshalAs(UnmanagedType.LPUTF8Str)]
            public string? EmotionModelPath;
        }
    }
}