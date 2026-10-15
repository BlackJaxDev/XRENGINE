using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Checks that the per-pass material bounds contract memo gives the same verdicts as the
/// certificate, and evaluates each published material state once in a pass.
/// </summary>
[TestFixture]
public sealed class PhysicsChainMaterialBoundsContractMemoTests
{
    private static XRMaterial TranslatingMaterial(float translation, out ShaderVector3 parameter)
    {
        parameter = new ShaderVector3(new Vector3(translation, 0.0f, 0.0f), "_VertexManipulationWorldTranslation");
        return new XRMaterial([new ShaderFloat(1.0f, "_VertexEffectsEnabled"), parameter]);
    }

    private static XRMaterial TranslatingMaterial(float translation)
        => TranslatingMaterial(translation, out _);

    private static void ShouldMatchCertificate(PhysicsChainMaterialBoundsContractMemo memo, float padding,
        bool supported, PhysicsChainDrawMaterialSnapshot snapshot, bool expected)
    {
        PhysicsChainMaterialBoundsCertificate.Covers(padding, supported, in snapshot).ShouldBe(expected);
        memo.Covers(padding, supported, in snapshot).ShouldBe(expected);
    }

    [Test]
    public void SharedMaterial_EvaluatesOncePerPass()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        PhysicsChainDrawMaterialSnapshot snapshot = PhysicsChainDrawMaterialSnapshot.Capture(TranslatingMaterial(0.1f));

        for (int row = 0; row < 8; ++row)
            memo.Covers(0.2f, true, in snapshot).ShouldBeTrue();
        // A smaller slot padding uses the same contract and does not contain the effect.
        memo.Covers(0.05f, true, in snapshot).ShouldBeFalse();

        memo.EvaluationCount.ShouldBe(1);
    }

    [Test]
    public void Clear_EvaluatesAgainInTheNextPass()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        PhysicsChainDrawMaterialSnapshot snapshot = PhysicsChainDrawMaterialSnapshot.Capture(TranslatingMaterial(0.1f));
        memo.Covers(0.2f, true, in snapshot).ShouldBeTrue();

        memo.Clear();
        memo.EvaluationCount.ShouldBe(0);
        memo.Covers(0.2f, true, in snapshot).ShouldBeTrue();

        memo.EvaluationCount.ShouldBe(1);
    }

    [Test]
    public void ChangedValue_RejectsTheOldSnapshotAndEvaluatesTheNewOne()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        XRMaterial material = TranslatingMaterial(0.1f, out ShaderVector3 translation);
        PhysicsChainDrawMaterialSnapshot published = PhysicsChainDrawMaterialSnapshot.Capture(material);
        memo.Covers(0.2f, true, in published).ShouldBeTrue();

        translation.Value = new Vector3(0.5f, 0.0f, 0.0f);
        PhysicsChainDrawMaterialSnapshot current = PhysicsChainDrawMaterialSnapshot.Capture(material);

        ShouldMatchCertificate(memo, 0.2f, true, published, expected: false);
        ShouldMatchCertificate(memo, 0.2f, true, current, expected: false);
        ShouldMatchCertificate(memo, 0.6f, true, current, expected: true);
        memo.EvaluationCount.ShouldBe(2);
    }

    [Test]
    public void RejectedSlot_FailsWithoutAnEvaluation()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        PhysicsChainDrawMaterialSnapshot snapshot = PhysicsChainDrawMaterialSnapshot.Capture(TranslatingMaterial(0.1f));

        ShouldMatchCertificate(memo, 0.2f, false, snapshot, expected: false);
        ShouldMatchCertificate(memo, -1.0f, true, snapshot, expected: false);
        ShouldMatchCertificate(memo, float.NaN, true, snapshot, expected: false);

        memo.EvaluationCount.ShouldBe(0);
    }

    [Test]
    public void RotationEffect_IsRejected()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        XRMaterial rotating = new([
            new ShaderFloat(1.0f, "_VertexEffectsEnabled"),
            new ShaderVector3(new Vector3(0.0f, 45.0f, 0.0f), "_VertexManipulationLocalRotation"),
        ]);
        PhysicsChainDrawMaterialSnapshot snapshot = PhysicsChainDrawMaterialSnapshot.Capture(rotating);

        ShouldMatchCertificate(memo, 100.0f, true, snapshot, expected: false);
        ShouldMatchCertificate(memo, 100.0f, true, snapshot, expected: false);
        memo.EvaluationCount.ShouldBe(1);
    }

    [Test]
    public void MoreMaterialsThanEntries_KeepCertificateVerdicts()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        var snapshots = new PhysicsChainDrawMaterialSnapshot[6];
        for (int index = 0; index < snapshots.Length; ++index)
            snapshots[index] = PhysicsChainDrawMaterialSnapshot.Capture(TranslatingMaterial(0.1f * (index + 1)));

        for (int pass = 0; pass < 2; ++pass)
        {
            for (int index = 0; index < snapshots.Length; ++index)
                ShouldMatchCertificate(memo, 0.35f, true, snapshots[index], expected: index < 3);
        }
    }

    [Test]
    public void NullMaterial_HasNoEffectPadding()
    {
        var memo = new PhysicsChainMaterialBoundsContractMemo();
        PhysicsChainDrawMaterialSnapshot snapshot = PhysicsChainDrawMaterialSnapshot.Capture(null);

        ShouldMatchCertificate(memo, 0.0f, true, snapshot, expected: true);
        ShouldMatchCertificate(memo, 0.0f, true, snapshot, expected: true);
        memo.EvaluationCount.ShouldBe(1);
    }
}
