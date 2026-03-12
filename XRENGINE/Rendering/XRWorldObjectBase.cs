using System.ComponentModel;
using MemoryPack;
using XREngine.Data.Core;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine
{
    [Serializable]
    public abstract class XRWorldObjectBase : RuntimeWorldObjectBase
    {
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public new XRWorldInstance? World
        {
            get => base.World as XRWorldInstance;
            internal set => SetWorldContext(value);
        }

        public new void ClearTicks()
            => base.ClearTicks();

        public void CopyFrom(XRWorldObjectBase newObj)
            => base.CopyFrom(newObj);
    }
}
