using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace XREngine.Components;

/// <summary>
/// AVX2 integration kernel for eight independent instances sharing one short
/// linear template. Constraint and publication phases reuse the scalar oracle
/// helpers so feature behavior and the scalar tail remain authoritative.
/// </summary>
internal static class PhysicsChainAvx2LinearBatchKernel
{
    public static bool TryStep8(
        PhysicsChainCpuRuntimeInstance a,
        PhysicsChainCpuRuntimeInstance b,
        PhysicsChainCpuRuntimeInstance c,
        PhysicsChainCpuRuntimeInstance d,
        PhysicsChainCpuRuntimeInstance e,
        PhysicsChainCpuRuntimeInstance f,
        PhysicsChainCpuRuntimeInstance g,
        PhysicsChainCpuRuntimeInstance h)
    {
        if (!Avx2.IsSupported || !ReferenceEquals(a.Template, b.Template) || !ReferenceEquals(a.Template, c.Template)
            || !ReferenceEquals(a.Template, d.Template) || !ReferenceEquals(a.Template, e.Template)
            || !ReferenceEquals(a.Template, f.Template) || !ReferenceEquals(a.Template, g.Template)
            || !ReferenceEquals(a.Template, h.Template))
            return false;
        if (a.Colliders.Length != 0 || b.Colliders.Length != 0 || c.Colliders.Length != 0 || d.Colliders.Length != 0
            || e.Colliders.Length != 0 || f.Colliders.Length != 0 || g.Colliders.Length != 0 || h.Colliders.Length != 0)
            return false;

        PhysicsChainTemplate template = a.Template;
        if ((template.FeatureMask & PhysicsChainTemplateFeatureMask.BranchedTopology) != 0)
            return false;
        if (!Validate(a) || !Validate(b) || !Validate(c) || !Validate(d)
            || !Validate(e) || !Validate(f) || !Validate(g) || !Validate(h))
            return false;

        ReadOnlySpan<PhysicsChainTemplateTree> trees = template.Trees.Span;
        ReadOnlySpan<PhysicsChainTemplateParticle> particles = template.Particles.Span;
        for (int treeIndex = 0; treeIndex < trees.Length; ++treeIndex)
            IntegrateTree(trees[treeIndex], treeIndex, particles, a, b, c, d, e, f, g, h);

        Finish(a); Finish(b); Finish(c); Finish(d); Finish(e); Finish(f); Finish(g); Finish(h);
        return true;
    }

    private static bool Validate(PhysicsChainCpuRuntimeInstance instance)
        => instance.Input.ResetState == 0u
        && PhysicsChainScalarReferenceKernel.ValidateContract(
            instance.Input, instance.Template.Trees.Span, instance.Template.Particles.Span,
            instance.TreeInputs, instance.ParticleInputs, instance.States, instance.Outputs);

    private static void Finish(PhysicsChainCpuRuntimeInstance instance)
    {
        float time = instance.Input.DeltaTime * instance.Input.Speed;
        PhysicsChainScalarReferenceKernel.ApplyConstraints(
            instance.Template, instance.ParticleInputs, instance.Colliders, instance.States, instance.Input, time);
        PhysicsChainScalarReferenceKernel.Publish(instance.States, instance.Outputs);
    }

    private static void IntegrateTree(
        in PhysicsChainTemplateTree tree,
        int treeIndex,
        ReadOnlySpan<PhysicsChainTemplateParticle> particles,
        PhysicsChainCpuRuntimeInstance a,
        PhysicsChainCpuRuntimeInstance b,
        PhysicsChainCpuRuntimeInstance c,
        PhysicsChainCpuRuntimeInstance d,
        PhysicsChainCpuRuntimeInstance e,
        PhysicsChainCpuRuntimeInstance f,
        PhysicsChainCpuRuntimeInstance g,
        PhysicsChainCpuRuntimeInstance h)
    {
        Vector3 fa = CalculateForce(a, treeIndex); Vector3 fb = CalculateForce(b, treeIndex);
        Vector3 fc = CalculateForce(c, treeIndex); Vector3 fd = CalculateForce(d, treeIndex);
        Vector3 fe = CalculateForce(e, treeIndex); Vector3 ff = CalculateForce(f, treeIndex);
        Vector3 fg = CalculateForce(g, treeIndex); Vector3 fh = CalculateForce(h, treeIndex);
        int end = tree.ParticleStart + tree.ParticleCount;
        for (int particleIndex = tree.ParticleStart; particleIndex < end; ++particleIndex)
        {
            PhysicsChainTemplateParticle particle = particles[particleIndex];
            if (particle.ParentIndex < 0)
            {
                SetRoot(a, particleIndex); SetRoot(b, particleIndex); SetRoot(c, particleIndex); SetRoot(d, particleIndex);
                SetRoot(e, particleIndex); SetRoot(f, particleIndex); SetRoot(g, particleIndex); SetRoot(h, particleIndex);
                continue;
            }
            IntegrateParticle(particleIndex, particle, fa, fb, fc, fd, fe, ff, fg, fh, a, b, c, d, e, f, g, h);
        }
    }

    private static Vector3 CalculateForce(PhysicsChainCpuRuntimeInstance instance, int treeIndex)
    {
        PhysicsChainCpuInput input = instance.Input;
        Vector3 force = input.Gravity;
        float lengthSquared = force.LengthSquared();
        if (lengthSquared > 1e-12f)
        {
            Vector3 direction = force / MathF.Sqrt(lengthSquared);
            force -= direction * MathF.Max(Vector3.Dot(instance.TreeInputs[treeIndex].RestGravity, direction), 0.0f);
        }
        return (force + input.ExternalForce) * (input.ObjectScale * input.DeltaTime * input.Speed);
    }

    private static void SetRoot(PhysicsChainCpuRuntimeInstance instance, int index)
    {
        ref PhysicsChainCpuState state = ref instance.States[index];
        state.PreviousPosition = state.Position;
        state.Position = instance.ParticleInputs[index].LocalToWorld.Translation;
    }

    private static void IntegrateParticle(
        int index, in PhysicsChainTemplateParticle particle,
        Vector3 fa, Vector3 fb, Vector3 fc, Vector3 fd, Vector3 fe, Vector3 ff, Vector3 fg, Vector3 fh,
        PhysicsChainCpuRuntimeInstance a, PhysicsChainCpuRuntimeInstance b, PhysicsChainCpuRuntimeInstance c, PhysicsChainCpuRuntimeInstance d,
        PhysicsChainCpuRuntimeInstance e, PhysicsChainCpuRuntimeInstance f, PhysicsChainCpuRuntimeInstance g, PhysicsChainCpuRuntimeInstance h)
    {
        Vector256<float> px = V(a.States[index].Position.X, b.States[index].Position.X, c.States[index].Position.X, d.States[index].Position.X, e.States[index].Position.X, f.States[index].Position.X, g.States[index].Position.X, h.States[index].Position.X);
        Vector256<float> py = V(a.States[index].Position.Y, b.States[index].Position.Y, c.States[index].Position.Y, d.States[index].Position.Y, e.States[index].Position.Y, f.States[index].Position.Y, g.States[index].Position.Y, h.States[index].Position.Y);
        Vector256<float> pz = V(a.States[index].Position.Z, b.States[index].Position.Z, c.States[index].Position.Z, d.States[index].Position.Z, e.States[index].Position.Z, f.States[index].Position.Z, g.States[index].Position.Z, h.States[index].Position.Z);
        Vector256<float> vx = Avx.Subtract(px, V(a.States[index].PreviousPosition.X, b.States[index].PreviousPosition.X, c.States[index].PreviousPosition.X, d.States[index].PreviousPosition.X, e.States[index].PreviousPosition.X, f.States[index].PreviousPosition.X, g.States[index].PreviousPosition.X, h.States[index].PreviousPosition.X));
        Vector256<float> vy = Avx.Subtract(py, V(a.States[index].PreviousPosition.Y, b.States[index].PreviousPosition.Y, c.States[index].PreviousPosition.Y, d.States[index].PreviousPosition.Y, e.States[index].PreviousPosition.Y, f.States[index].PreviousPosition.Y, g.States[index].PreviousPosition.Y, h.States[index].PreviousPosition.Y));
        Vector256<float> vz = Avx.Subtract(pz, V(a.States[index].PreviousPosition.Z, b.States[index].PreviousPosition.Z, c.States[index].PreviousPosition.Z, d.States[index].PreviousPosition.Z, e.States[index].PreviousPosition.Z, f.States[index].PreviousPosition.Z, g.States[index].PreviousPosition.Z, h.States[index].PreviousPosition.Z));
        Vector256<float> damping = V(Damping(a, index, particle), Damping(b, index, particle), Damping(c, index, particle), Damping(d, index, particle), Damping(e, index, particle), Damping(f, index, particle), Damping(g, index, particle), Damping(h, index, particle));
        Vector256<float> retain = Avx.Subtract(Vector256.Create(1.0f), damping);
        Vector256<float> mx = V(Move(a, particle).X, Move(b, particle).X, Move(c, particle).X, Move(d, particle).X, Move(e, particle).X, Move(f, particle).X, Move(g, particle).X, Move(h, particle).X);
        Vector256<float> my = V(Move(a, particle).Y, Move(b, particle).Y, Move(c, particle).Y, Move(d, particle).Y, Move(e, particle).Y, Move(f, particle).Y, Move(g, particle).Y, Move(h, particle).Y);
        Vector256<float> mz = V(Move(a, particle).Z, Move(b, particle).Z, Move(c, particle).Z, Move(d, particle).Z, Move(e, particle).Z, Move(f, particle).Z, Move(g, particle).Z, Move(h, particle).Z);
        StorePrevious(index, px, py, pz, mx, my, mz, a, b, c, d, e, f, g, h);
        StorePosition(index,
            Avx.Add(Avx.Add(Avx.Add(px, Avx.Multiply(vx, retain)), V(fa.X, fb.X, fc.X, fd.X, fe.X, ff.X, fg.X, fh.X)), mx),
            Avx.Add(Avx.Add(Avx.Add(py, Avx.Multiply(vy, retain)), V(fa.Y, fb.Y, fc.Y, fd.Y, fe.Y, ff.Y, fg.Y, fh.Y)), my),
            Avx.Add(Avx.Add(Avx.Add(pz, Avx.Multiply(vz, retain)), V(fa.Z, fb.Z, fc.Z, fd.Z, fe.Z, ff.Z, fg.Z, fh.Z)), mz),
            a, b, c, d, e, f, g, h);
    }

    private static float Damping(PhysicsChainCpuRuntimeInstance instance, int index, in PhysicsChainTemplateParticle particle)
    {
        float value = particle.Damping + (instance.States[index].IsColliding != 0u ? particle.Friction : 0.0f);
        instance.States[index].IsColliding = 0u;
        return Math.Clamp(value, 0.0f, 1.0f);
    }

    private static Vector3 Move(PhysicsChainCpuRuntimeInstance instance, in PhysicsChainTemplateParticle particle)
        => instance.Input.ObjectMove * particle.Inert;

    private static Vector256<float> V(float a, float b, float c, float d, float e, float f, float g, float h)
        => Vector256.Create(a, b, c, d, e, f, g, h);

    private static void StorePrevious(
        int index, Vector256<float> px, Vector256<float> py, Vector256<float> pz,
        Vector256<float> mx, Vector256<float> my, Vector256<float> mz,
        PhysicsChainCpuRuntimeInstance a, PhysicsChainCpuRuntimeInstance b, PhysicsChainCpuRuntimeInstance c, PhysicsChainCpuRuntimeInstance d,
        PhysicsChainCpuRuntimeInstance e, PhysicsChainCpuRuntimeInstance f, PhysicsChainCpuRuntimeInstance g, PhysicsChainCpuRuntimeInstance h)
    {
        StorePreviousLane(a, index, 0, px, py, pz, mx, my, mz); StorePreviousLane(b, index, 1, px, py, pz, mx, my, mz);
        StorePreviousLane(c, index, 2, px, py, pz, mx, my, mz); StorePreviousLane(d, index, 3, px, py, pz, mx, my, mz);
        StorePreviousLane(e, index, 4, px, py, pz, mx, my, mz); StorePreviousLane(f, index, 5, px, py, pz, mx, my, mz);
        StorePreviousLane(g, index, 6, px, py, pz, mx, my, mz); StorePreviousLane(h, index, 7, px, py, pz, mx, my, mz);
    }

    private static void StorePreviousLane(PhysicsChainCpuRuntimeInstance lane, int index, int element, Vector256<float> px, Vector256<float> py, Vector256<float> pz, Vector256<float> mx, Vector256<float> my, Vector256<float> mz)
        => lane.States[index].PreviousPosition = new Vector3(px.GetElement(element) + mx.GetElement(element), py.GetElement(element) + my.GetElement(element), pz.GetElement(element) + mz.GetElement(element));

    private static void StorePosition(
        int index, Vector256<float> x, Vector256<float> y, Vector256<float> z,
        PhysicsChainCpuRuntimeInstance a, PhysicsChainCpuRuntimeInstance b, PhysicsChainCpuRuntimeInstance c, PhysicsChainCpuRuntimeInstance d,
        PhysicsChainCpuRuntimeInstance e, PhysicsChainCpuRuntimeInstance f, PhysicsChainCpuRuntimeInstance g, PhysicsChainCpuRuntimeInstance h)
    {
        StorePositionLane(a, index, 0, x, y, z); StorePositionLane(b, index, 1, x, y, z);
        StorePositionLane(c, index, 2, x, y, z); StorePositionLane(d, index, 3, x, y, z);
        StorePositionLane(e, index, 4, x, y, z); StorePositionLane(f, index, 5, x, y, z);
        StorePositionLane(g, index, 6, x, y, z); StorePositionLane(h, index, 7, x, y, z);
    }

    private static void StorePositionLane(PhysicsChainCpuRuntimeInstance lane, int index, int element, Vector256<float> x, Vector256<float> y, Vector256<float> z)
        => lane.States[index].Position = new Vector3(x.GetElement(element), y.GetElement(element), z.GetElement(element));
}
