namespace XREngine.Data.Rendering
{
    /// <summary>
    /// API-agnostic sampler comparison functions.
    /// </summary>
    public enum ETextureCompareFunc
    {
        /// <summary>
        /// No comparison is performed.
        /// </summary>
        Never,
        /// <summary>
        /// Passes if the incoming value is less than the stored value.
        /// </summary>
        Less,
        /// <summary>
        /// Passes if the incoming value is equal to the stored value.
        /// </summary>
        Equal,
        /// <summary>
        /// Passes if the incoming value is less than or equal to the stored value.
        /// </summary>
        LessOrEqual,
        /// <summary>
        /// Passes if the incoming value is greater than the stored value.
        /// </summary>
        Greater,
        /// <summary>
        /// Passes if the incoming value is not equal to the stored value.
        /// </summary>
        NotEqual,
        /// <summary>
        /// Passes if the incoming value is greater than or equal to the stored value.
        /// </summary>
        GreaterOrEqual,
        /// <summary>
        /// Always passes.
        /// </summary>
        Always,
    }
}
