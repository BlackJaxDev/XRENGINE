using XREngine.Data.Core;

namespace XREngine.Rendering.Models.Materials
{
    [Serializable]
    public class StencilTestFace : XRBase
    {
        private EStencilOp _bothFailOp = EStencilOp.Keep;
        private EStencilOp _stencilPassDepthFailOp = EStencilOp.Keep;
        private EStencilOp _bothPassOp = EStencilOp.Keep;
        private EComparison _function;
        private int _reference;
        private uint _readMask;
        private uint _writeMask;

        /// <summary>
        /// What to do to the stencil buffer if both the stencil and depth tests fail.
        /// </summary>
        public EStencilOp BothFailOp
        {
            get => _bothFailOp;
            set => SetField(ref _bothFailOp, value);
        }
        /// <summary>
        /// What to do to the stencil buffer if the stencil test passes but the depth test fails.
        /// </summary>
        public EStencilOp StencilPassDepthFailOp
        {
            get => _stencilPassDepthFailOp;
            set => SetField(ref _stencilPassDepthFailOp, value);
        }
        /// <summary>
        /// What to do to the stencil buffer if both the stencil and depth tests pass.
        /// </summary>
        public EStencilOp BothPassOp
        {
            get => _bothPassOp;
            set => SetField(ref _bothPassOp, value);
        }
        /// <summary>
        /// The comparison function to use for the stencil test.
        /// </summary>
        public EComparison Function
        {
            get => _function;
            set => SetField(ref _function, value);
        }
        /// <summary>
        /// The reference value to use for the stencil test.
        /// </summary>
        public int Reference
        {
            get => _reference;
            set => SetField(ref _reference, value);
        }
        /// <summary>
        /// The mask to use when reading the stencil buffer.
        /// Only the bits set in this mask will be compared to the reference value.
        /// This mask is bitwise ANDed with both the reference value and the stored stencil value before the test compares them.
        /// </summary>
        public uint ReadMask
        {
            get => _readMask;
            set => SetField(ref _readMask, value);
        }
        /// <summary>
        /// The mask to use when writing to the stencil buffer.
        /// This masks out the bits that are allowed to be written to the stencil buffer.
        /// </summary>
        public uint WriteMask
        {
            get => _writeMask;
            set => SetField(ref _writeMask, value);
        }

        public override string ToString()
            => $"{Function} Ref:{Reference} Read Mask:{ReadMask} Write Mask:{WriteMask}";
    }
}
