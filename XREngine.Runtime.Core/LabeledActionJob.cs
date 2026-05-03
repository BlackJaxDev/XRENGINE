using System;
using System.Collections;

namespace XREngine
{
    /// <summary>
    /// Simple job that executes a single action with a profiler-friendly label.
    /// </summary>
    public sealed class LabeledActionJob(Action action, string label) : Job
    {
        private readonly Action _action = action ?? throw new ArgumentNullException(nameof(action));
        private readonly string _label = string.IsNullOrWhiteSpace(label) ? "MainThreadInvoke" : label.Trim();

        public override IEnumerable Process()
        {
            _action();
            yield break;
        }

        internal override string GetProfilerLabel()
            => $"Invoke:{_label}";
    }
}