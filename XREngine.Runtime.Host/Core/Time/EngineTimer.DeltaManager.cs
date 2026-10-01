using XREngine.Extensions;
using System;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine.Timers
{
    public partial class EngineTimer
    {
        public class DeltaManager : XRBase
        {
            /// <summary>
            /// Gets a float representing the frequency of frame events, in hertz (updates per second).
            /// Max value is clamped to 10k.
            /// </summary>
            public float FrameRate => 1.0f / Delta.ClampMin(0.0001f);

            private float _dilation = 1.0f;
            /// <summary>
            /// Delta multiplier. Default is 1.0f.
            /// </summary>
            public float Dilation
            {
                get => _dilation;
                set => SetField(ref _dilation, value);
            }

            private float _deltaSmoothingSpeed = 0.1f;
            /// <summary>
            /// How quickly the smoothed delta should change to the current delta every frame.
            /// Clamps to a value between 0.0f and 1.0f.
            /// </summary>
            public float DeltaSmoothingSpeed
            {
                get => _deltaSmoothingSpeed;
                set => SetField(ref _deltaSmoothingSpeed, value.Clamp(0.0f, 1.0f));
            }

            private long _deltaTicks = SecondsToStopwatchTicks(0.016f);
            /// <summary>
            /// The amount of time that has passed since the last frame, in seconds.
            /// </summary>
            public float Delta
            {
                get => TicksToSeconds(_deltaTicks);
                set => DeltaTicks = SecondsToStopwatchTicks(value.ClampMin(0.0f));
            }

            public long DeltaTicks
            {
                get => _deltaTicks;
                set
                {
                    _deltaTicks = Math.Max(0L, value);
                    SmoothedDelta = Interp.Lerp(SmoothedDelta, TicksToSeconds(_deltaTicks), DeltaSmoothingSpeed);
                }
            }

            public float DilatedDelta => Delta * Dilation;
            public float SmoothedDelta { get; private set; }
            public float SmoothedDilatedDelta => SmoothedDelta * Dilation;

            private long _lastTimestampTicks;
            public float LastTimestamp
            {
                get => TicksToSeconds(_lastTimestampTicks);
                set => _lastTimestampTicks = SecondsToStopwatchTicks(value.ClampMin(0.0f));
            }

            public long LastTimestampTicks
            {
                get => _lastTimestampTicks;
                set => _lastTimestampTicks = Math.Max(0L, value);
            }

            /// <summary>
            /// This is the time that has passed running calculations of the last frame, in seconds.
            /// </summary>
            private long _elapsedTicks;
            public float ElapsedTime
            {
                get => TicksToSeconds(_elapsedTicks);
                set => _elapsedTicks = SecondsToStopwatchTicks(value.ClampMin(0.0f));
            }

            public long ElapsedTicks
            {
                get => _elapsedTicks;
                set => _elapsedTicks = Math.Max(0L, value);
            }
        }
    }
}
