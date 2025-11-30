using System.Collections;

namespace XREngine
{
    public sealed class EnumeratorJob : Job
    {
        private readonly Func<IEnumerable> _routineFactory;

        public EnumeratorJob(
            IEnumerable routine,
            Action<float>? onProgress = null,
            Action? onCompleted = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float, object?>? onProgressWithPayload = null)
        {
            _routineFactory = routine is null ? throw new ArgumentNullException(nameof(routine)) : () => routine;
            HookCallbacks(onProgress, onCompleted, onError, onCanceled, onProgressWithPayload);
        }

        public EnumeratorJob(
            Func<IEnumerable> routineFactory,
            Action<float>? onProgress = null,
            Action? onCompleted = null,
            Action<Exception>? onError = null,
            Action? onCanceled = null,
            Action<float, object?>? onProgressWithPayload = null)
        {
            _routineFactory = routineFactory ?? throw new ArgumentNullException(nameof(routineFactory));
            HookCallbacks(onProgress, onCompleted, onError, onCanceled, onProgressWithPayload);
        }

        public override IEnumerable Process()
            => _routineFactory();

        private void HookCallbacks(
            Action<float>? progress,
            Action? completed,
            Action<Exception>? error,
            Action? canceled,
            Action<float, object?>? progressWithPayload)
        {
            if (progress != null)
                ProgressChanged += (_, value) => progress(value);
            if (progressWithPayload != null)
                ProgressWithPayload += (_, value, payload) => progressWithPayload(value, payload);
            if (completed != null)
                Completed += _ => completed();
            if (canceled != null)
                Canceled += _ => canceled();
            if (error != null)
                Faulted += (_, ex) => error(ex);
        }
    }
}
