//This has been converted from C++ and modified.
//Original source:
//http://users.telenet.be/tfautre/softdev/tristripper/


namespace XREngine.TriangleConverter
{
    public class Policy
    {
        public Policy(uint MinStripSize, bool Cache)
        {
            _MinStripSize = MinStripSize;
            _Cache = Cache;
        }

        public Strip BestStrip { get { return _Strip; } }
        public void Challenge(Strip Strip, uint Degree, uint CacheHits)
        {
            if (Strip.Size < _MinStripSize)
                return;

            if (!_Cache)
            {
                //Cache is disabled, take the longest strip
                if (Strip.Size > _Strip.Size)
                    _Strip = Strip;
            }
            else
            {
                //Cache simulator enabled
                if (CacheHits > _CacheHits)
                {
                    //Priority 1: Keep the strip with the best cache hit count
                    _Strip = Strip;
                    _Degree = Degree;
                    _CacheHits = CacheHits;
                }
                else if ((CacheHits == _CacheHits) && (((_Strip.Size != 0) && (Degree < _Degree)) || (Strip.Size > _Strip.Size)))
                {
                    //Priority 2: Keep the strip with the loneliest start triangle
                    //Priority 3: Keep the longest strip
                    _Strip = Strip;
                    _Degree = Degree;
                }
            }
        }

        private Strip _Strip = new Strip();
        private uint _Degree;
        private uint _CacheHits;

        private uint _MinStripSize;
        private bool _Cache;
    }
}
