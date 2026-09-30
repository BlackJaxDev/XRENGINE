namespace XREngine.Scene.Physics;

/// <summary>Decodes a height-map image for physics authoring.</summary>
public interface IPhysicsHeightFieldImageSource
{
    PhysicsHeightFieldImage Load(string imagePath);
}
