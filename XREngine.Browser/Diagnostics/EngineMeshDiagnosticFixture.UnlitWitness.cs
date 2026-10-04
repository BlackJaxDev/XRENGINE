using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Browser.Diagnostics;

internal sealed partial class EngineMeshDiagnosticFixture
{
    // The sloping shared edge spans many pixel phases. Qualification discovers
    // partial coverage from resolved HDR instead of assuming GPU sample positions.
    // The region stays inside the gutter and both silhouettes at every admitted size.
    private const string UnlitWitnessMetadata = """
        {"name":"gutter-overlap-edge","semantic":"UnlitColorV1","factory":"CreateUnlitColorMaterialForward",
         "roi":{"left":0.09,"top":0.312,"right":0.91,"bottom":0.358},
         "rear":{"color":[0.25,0.5,0.75,1],"normal":[0,0.7071067811865476,0.7071067811865476],
                 "encodedNormal":[0.5,0.75,0,1],"worldZ":-2.5,"normalDepth":0.625,
                 "vertices":[[-0.86,0.26],[0.86,0.26],[0.86,0.40],[-0.86,0.40]]},
         "front":{"color":[1.25,0.25,0.125,0.5],"normal":[0.7071067811865476,0,0.7071067811865476],
                  "encodedNormal":[0.75,0.5,0,1],"worldZ":-1.5,"normalDepth":0.375,
                  "vertices":[[-0.86,0.285],[0.86,0.375],[0.86,0.40],[-0.86,0.40]]},
         "cameraNear":0,"cameraFar":4,"coverageDenominator":4}
        """;

    private void InitializeUnlitWitness()
    {
        Vector3 rearNormal = Vector3.Normalize(new Vector3(0, 1, 1));
        Vector3 frontNormal = Vector3.Normalize(new Vector3(1, 0, 1));
        XRMesh rear = CreateUnlitWitnessMesh(0.26f, 0.26f, rearNormal);
        _unlitWitnessMeshes.Add(rear);
        AddUnlitModel("Unlit gutter rear surface", rear,
            XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.25f, 0.5f, 0.75f, 1)), new Vector3(0, 0, -2.5f));
        XRMesh front = CreateUnlitWitnessMesh(0.285f, 0.375f, frontNormal);
        _unlitWitnessMeshes.Add(front);
        AddUnlitModel("Unlit gutter front edge", front,
            XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(1.25f, 0.25f, 0.125f, 0.5f)), new Vector3(0, 0, -1.5f));
    }

    private static XRMesh CreateUnlitWitnessMesh(float lowerLeft, float lowerRight, Vector3 normal)
        => new(
            [new Vertex(new Vector3(-0.86f, lowerLeft, 0), normal),
             new Vertex(new Vector3(0.86f, lowerRight, 0), normal),
             new Vertex(new Vector3(0.86f, 0.40f, 0), normal),
             new Vertex(new Vector3(-0.86f, 0.40f, 0), normal)],
            new List<ushort> { 0, 1, 2, 0, 2, 3 });
}
