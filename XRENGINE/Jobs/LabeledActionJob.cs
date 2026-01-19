using System;
using System.Collections;

namespace XREngine
{
    /// <summary>
    /// Simple job that executes a single action with a profiler-friendly label.
    /// </summary>
    internal sealed class LabeledActionJob : Job
    {
        private readonly Action _action;
        private readonly string _label;

        public LabeledActionJob(Action action, string label)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
            _label = string.IsNullOrWhiteSpace(label) ? "MainThreadInvoke" : label.Trim();
        }

        public override IEnumerable Process()
        {
            _action();
            yield break;
        }

        internal override string GetProfilerLabel()
            => $"Invoke:{_label}";
    }
}
