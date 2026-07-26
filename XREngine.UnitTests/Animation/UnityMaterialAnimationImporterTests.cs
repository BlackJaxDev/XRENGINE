using NUnit.Framework;
using Shouldly;
using XREngine.Animation;
using XREngine.Animation.Importers;

namespace XREngine.UnitTests.Animation;

[TestFixture]
public sealed class UnityMaterialAnimationImporterTests
{
    [Test]
    public void Import_PreservesUnlockedAndLockedMaterialCurveBindings()
    {
        const string yaml = """
AnimationClip:
  m_Name: MaterialCurves
  m_SampleRate: 60
  m_AnimationClipSettings:
    m_StartTime: 0
    m_StopTime: 1
    m_LoopTime: 1
  m_FloatCurves:
    - path: Body
      attribute: material._Color.r
      classID: 23
      curve:
        m_Curve:
          - time: 0
            value: 0.25
            inSlope: 1
            outSlope: 2
            tangentMode: 1
    - path: Body
      attribute: m_Materials.Array.data[2]._EmissionStrength_A1B2C3
      classID: 23
      curve:
        m_Curve:
          - time: 0
            value: 3
            inSlope: 0
            outSlope: 0
            tangentMode: 0
  m_PPtrCurves:
    - path: Body
      attribute: materials.Array.data[1]._MainTex
      classID: 23
      curve: []
""";

        AnimationClip clip = Import(yaml);

        clip.SourceMaterialBindings.Length.ShouldBe(3);
        UnityMaterialAnimationBinding color = clip.SourceMaterialBindings
            .Single(x => x.SourceProperty == "_Color");
        color.MaterialSlot.ShouldBe(0);
        color.Component.ShouldBe(0);
        color.ValueKind.ShouldBe(UnityMaterialAnimationValueKind.Color);

        UnityMaterialAnimationBinding locked = clip.SourceMaterialBindings
            .Single(x => x.SourceProperty == "_EmissionStrength_A1B2C3");
        locked.MaterialSlot.ShouldBe(2);
        locked.Component.ShouldBe(-1);
        locked.OriginalAttribute.ShouldContain("_EmissionStrength_A1B2C3");

        UnityMaterialAnimationBinding texture = clip.SourceMaterialBindings
            .Single(x => x.SourceProperty == "_MainTex");
        texture.MaterialSlot.ShouldBe(1);
        texture.ValueKind.ShouldBe(UnityMaterialAnimationValueKind.Texture);
        clip.MaterialBindingDiagnostics.Single().ShouldContain("XRTexture");

        Enumerate(clip.RootMember!).Count(x => x.MemberName == "GetMaterialAnimationBinding")
            .ShouldBe(2);
        Enumerate(clip.RootMember!).Count(x => x.MemberName == "SetFloat")
            .ShouldBe(2);
    }

    private static AnimationClip Import(string yaml)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"material-{Guid.NewGuid():N}.anim");
        File.WriteAllText(path, yaml);
        try
        {
            return AnimYamlImporter.Import(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IEnumerable<AnimationMember> Enumerate(AnimationMember root)
    {
        yield return root;
        foreach (AnimationMember child in root.Children)
        foreach (AnimationMember descendant in Enumerate(child))
            yield return descendant;
    }
}
