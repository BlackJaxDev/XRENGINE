namespace XREngine.Data.Rendering
{
    public enum EBufferUsage
    {
        /// <summary>
        /// Write-only buffer that is constantly changed.
        /// </summary>
        StreamDraw = 0,
        /// <summary>
        /// Read-only buffer that is constantly changed.
        /// </summary>
        StreamRead = 1,
        /// <summary>
        /// Two-way buffer that is constantly changed.
        /// </summary>
        StreamCopy = 2,
        /// <summary>
        /// Write-only buffer that is not changed.
        /// </summary>
        StaticDraw = 4,
        /// <summary>
        /// Read-only buffer that is not changed.
        /// </summary>
        StaticRead = 5,
        /// <summary>
        /// Two-way static buffer that is not changed.
        /// </summary>
        StaticCopy = 6,
        /// <summary>
        /// Write-only buffer that is changed semi-often.
        /// </summary>
        DynamicDraw = 8,
        /// <summary>
        /// Read-only buffer that is changed semi-often.
        /// </summary>
        DynamicRead = 9,
        /// <summary>
        /// Two-way buffer that is changed semi-often.
        /// </summary>
        DynamicCopy = 10
    }
}
