using System;
using System.Collections;

namespace XREngine
{
    /// <summary>
    /// Coroutine job with a profiler-friendly label.
    /// </summary>
    public sealed class LabeledCoroutineJob : Job
    {
        private readonly Func<bool> _tick;
        private readonly string _label;

        public LabeledCoroutineJob(Func<bool> tick, string label)
        {
            _tick = tick ?? throw new ArgumentNullException(nameof(tick));
            _label = string.IsNullOrWhiteSpace(label) ? "MainThreadCoroutine" : label.Trim();
        }

        public override IEnumerable Process()
        {
            while (!_tick())
            {
                yield return WaitForNextDispatch.Instance;
            }
        }

        internal override string GetProfilerLabel()
            => $"Coroutine:{_label}";
    }
}