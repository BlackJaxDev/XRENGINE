using System;
using System.Collections;

namespace XREngine
{
    /// <summary>
    /// Simple job that executes a single action.
    /// </summary>
    public sealed class ActionJob : Job
    {
        private readonly Action _action;

        public ActionJob(Action action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        public override IEnumerable Process()
        {
            _action();
            yield break;
        }

        internal override string GetProfilerLabel()
        {
            var method = _action.Method;
            string typeName = method.DeclaringType?.Name ?? "<static>";
            return $"{GetType().Name}:{typeName}.{method.Name}";
        }
    }
}