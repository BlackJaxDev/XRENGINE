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
    public class ThreadSafeListEnumerator<T>(List<T> list, ReaderWriterLockSlim? locker) : IEnumerator<T>
    {
        private int _currentIndex = -1;  //Start at -1 per standard enumerator pattern
        private bool _lockHeld = false;

        public T Current => _currentIndex < 0 || _currentIndex >= list.Count
            ? throw new InvalidOperationException()
            : list[_currentIndex];

        object IEnumerator.Current => Current!;

        public void Dispose()
        {
            if (_lockHeld)
            {
                locker?.ExitReadLock();
                _lockHeld = false;
            }
            GC.SuppressFinalize(this);
        }

        public bool MoveNext()
        {
            try
            {
                if (_currentIndex == -1)
                {
                    locker?.EnterReadLock();
                    _lockHeld = true;
                }

                _currentIndex++;
                return _currentIndex < list.Count;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Reset()
        {
            if (_lockHeld)
            {
                locker?.ExitReadLock();
                _lockHeld = false;
            }
            _currentIndex = -1;
        }
    }
}
