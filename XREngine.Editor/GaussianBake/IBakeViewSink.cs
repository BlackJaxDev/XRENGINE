namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Takes the views of a <see cref="BakeViewCapture"/> one at a time, so that a long capture does not keep all views in
/// memory. The capture calls every member on the render thread.
/// </summary>
internal interface IBakeViewSink
{
    /// <summary>
    /// Gets a value that tells the capture that the content is ready to render. The warm-up continues until it is true,
    /// and the capture fails when it is still false at the end of the warm-up time.
    /// </summary>
    bool IsContentReady { get; }

    /// <summary>
    /// Gets a value that tells the capture that the sink can take one more view now. When it is false, the capture
    /// renders nothing in this dispatch and asks again in the next one.
    /// </summary>
    bool CanAccept { get; }

    /// <summary>Sets the scene state of a view. The capture calls it before each render of the view, warm-up renders included.</summary>
    /// <param name="viewIndex">The index of the pose.</param>
    void PrepareView(int viewIndex);

    /// <summary>Takes one view. The sink owns the array.</summary>
    /// <param name="viewIndex">The index of the pose.</param>
    /// <param name="rgba">The linear RGBA pixels, four floats each, top row first.</param>
    void Accept(int viewIndex, float[] rgba);
}
