namespace System.Collections.Generic
{
    public class ThreadSafeEnumerator<T> : IEnumerator<T>, IDisposable
    {
        private readonly IEnumerator<T> _inner;
        private readonly ReaderWriterLockSlim _lock;

        public ThreadSafeEnumerator(IEnumerator<T> inner, ReaderWriterLockSlim rwlock)
        {
            _inner = inner;
            _lock = rwlock;
            _lock.EnterReadLock();
        }
        public void Dispose()
        {
            _lock.ExitReadLock();
            GC.SuppressFinalize(this);
        }

        public bool MoveNext() => _inner.MoveNext();
        public void Reset() => _inner.Reset();
        public T Current => _inner.Current;
        object IEnumerator.Current => Current!;
    }
    public class ThreadSafeListEnumerator<T> : IEnumerator<T>
    {
        private readonly T[] _snapshot;
        private int _currentIndex = -1;  // Start at -1 per standard enumerator pattern

        public ThreadSafeListEnumerator(List<T> list, ReaderWriterLockSlim? locker)
        {
            if (locker != null)
            {
                locker.EnterReadLock();
                try
                {
                    _snapshot = list.ToArray();
                }
                finally
                {
                    locker.ExitReadLock();
                }
            }
            else
            {
                _snapshot = list.ToArray();
            }
        }

        public T Current => _currentIndex < 0 || _currentIndex >= _snapshot.Length
            ? throw new InvalidOperationException()
            : _snapshot[_currentIndex];

        object IEnumerator.Current => Current!;

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        public bool MoveNext()
        {
            _currentIndex++;
            return _currentIndex < _snapshot.Length;
        }

        public void Reset()
        {
            _currentIndex = -1;
        }
    }
}
