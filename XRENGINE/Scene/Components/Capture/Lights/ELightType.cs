namespace XREngine.Scene.Components.Capture.Lights
{
    /// <summary>
    /// Determines how the light is handled by the engine for optimization purposes.
    /// </summary>
    public enum ELightType
    {
        //Movable. Always calculates light for everything per-frame.
        Dynamic,
        //Moveable. Bakes into shadow maps when not moving.
        DynamicCached,
        //Does not move. Allows baking light into shadow maps.
        Static,
    }
}
