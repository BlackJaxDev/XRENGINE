namespace XREngine.Rendering.Meshlets;

/// <summary>Installs native mesh processing for desktop authoring and cook hosts.</summary>
public static class MeshOptimizerBackend
{
    public static void Register() => MeshOptimizerBackendServices.Install(new NativeMeshOptimizerBackend());
}
