//This has been converted from C++ and modified.
//Original source:
//http://users.telenet.be/tfautre/softdev/tristripper/

namespace XREngine.TriangleConverter
{
    class CacheSimulator
    {
        protected Deque<uint> _cache;
	    protected uint _numHits;
	    protected bool _pushHits;

	    public CacheSimulator() 
        {
            _numHits = 0;
            _pushHits = true;
            _cache = new Deque<uint>();
        }
	    public void Clear()
        {
            ResetHitCount();
	        _cache.Clear();
        }
	    public void Resize(uint Size)
        {
            //_Cache.Resize(Size, uint.MaxValue);

            _cache.Clear();
            for (int x = 0; x < Size; x++)
                _cache.PushFront(uint.MaxValue);
        }
	    public void Reset()
        {
            _cache.Clear();

            for (int x = 0; x < _cache.Count; x++)
                _cache.PushFront(uint.MaxValue);
               
	        ResetHitCount();
        }
	    public void PushCacheHits(bool enabled = true)
        {
            _pushHits = enabled;
        }
	    public uint Size { get { return (uint)_cache.Count; } }
	    public void Push(uint i, bool countCacheHit = false)
        {
            if ((countCacheHit || _pushHits) && _cache.Contains(i))
            {
			    // Should we count the cache hits?
			    if (countCacheHit) _numHits++;
			
			    // Should we not push the index into the cache if it's a cache hit?
			    if (!_pushHits)
				    return;
		    }
	        
	        // Manage the indices cache as a FIFO structure
	        _cache.PushFront(i);
	        _cache.PopBack();
        }
	    public void Merge(CacheSimulator backward, uint possibleOverlap)
        {
            uint Overlap = Math.Min(possibleOverlap, Size);

	        for (uint i = 0; i < Overlap; ++i)
		        Push(backward._cache[i], true);

	        _numHits += backward._numHits;
        }

        public void ResetHitCount() { _numHits = 0; }
        public uint HitCount { get { return _numHits; } }
    }
}
