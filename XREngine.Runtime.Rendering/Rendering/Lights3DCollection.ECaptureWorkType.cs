namespace XREngine.Scene
{
    public partial class Lights3DCollection
    {
        #region Capture Work Items

        public enum ECaptureWorkType : byte
        {
            /// <summary>Render a single cubemap face (collect + swap + render).</summary>
            CubemapFace,
            /// <summary>Finalize a cubemap capture cycle (mip gen, octa encode, IBL).</summary>
            CaptureFinalize,
            /// <summary>Full non-progressive capture (all faces + finalize in one call).</summary>
            FullCapture,
        }

        #endregion
    }
}
