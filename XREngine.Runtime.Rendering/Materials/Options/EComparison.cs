namespace XREngine.Rendering.Models.Materials
{
    /// <summary>
    /// Defines stencil and depth test comparison functions.
    /// </summary>
    public enum EComparison
    {
        /// <summary>
        /// The test never passes (always fails).
        /// </summary>
        Never,

        /// <summary>
        /// Passes if the reference value is less than the stencil value.
        /// </summary>
        Less,

        /// <summary>
        /// Passes if the reference value equals the stencil value.
        /// </summary>
        Equal,

        /// <summary>
        /// Passes if the reference value is less than or equal to the stencil value.
        /// </summary>
        Lequal,

        /// <summary>
        /// Passes if the reference value is greater than the stencil value.
        /// </summary>
        Greater,

        /// <summary>
        /// Passes if the reference value is not equal to the stencil value.
        /// </summary>
        Nequal,

        /// <summary>
        /// Passes if the reference value is greater than or equal to the stencil value.
        /// </summary>
        Gequal,

        /// <summary>
        /// The test always passes (never fails).
        /// </summary>
        Always
    }
}
