using System;
using System.Collections;

namespace XREngine
{
    /// <summary>
    /// Main-thread coroutine job that ticks until the callback returns true.
    /// </summary>
    internal sealed class CoroutineJob : Job
    {
        private readonly Func<bool> _tick;

        public CoroutineJob(Func<bool> tick)
        {
            _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        }

        public override IEnumerable Process()
        {
            while (!_tick())
            {
                yield return WaitForNextDispatch.Instance;
            }
        }

        internal override string GetProfilerLabel()
        {
            var method = _tick.Method;
            string typeName = method.DeclaringType?.Name ?? "<static>";
            return $"{GetType().Name}:{typeName}.{method.Name}";
        }
    }
}
