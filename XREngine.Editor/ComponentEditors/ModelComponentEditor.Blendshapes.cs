using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;

namespace XREngine.Editor.ComponentEditors;

public sealed partial class ModelComponentEditor
{
    private static readonly ConditionalWeakTable<XRMesh, BlendshapeEffectState> s_blendshapeEffectStates = new();

    private sealed class BlendshapeEffectState
    {
        public XRMeshBlendshapeEffectSnapshot? Snapshot;
    }

    private static void RefreshEffectiveBlendshapeNames(ModelComponent modelComponent, HashSet<string> namesWithEffect)
    {
        namesWithEffect.Clear();
        foreach (RenderableMesh runtimeMesh in modelComponent.Meshes)
        {
            foreach (var lod in runtimeMesh.LODs)
            {
                if (lod.Renderer is not XRMeshRenderer renderer || renderer.Mesh is not { HasBlendshapes: true } mesh)
                    continue;

                string[] names = mesh.BlendshapeNames;
                XRMeshBlendshapeEffectSnapshot? effects = GetBlendshapeEffects(mesh);
                for (int i = 0; i < names.Length; i++)
                {
                    if (effects is null || effects.HasEffect(i))
                        namesWithEffect.Add(names[i] ?? $"Blendshape {i}");
                }
            }
        }
    }

    private static XRMeshBlendshapeEffectSnapshot? GetBlendshapeEffects(XRMesh mesh)
    {
        BlendshapeEffectState state = s_blendshapeEffectStates.GetValue(mesh, _ => new BlendshapeEffectState());
        if (state.Snapshot is { } snapshot && snapshot.IsCurrent(mesh))
            return snapshot;

        if (!XRMeshBlendshapeEffectSnapshot.TryCreate(mesh, out snapshot))
        {
            state.Snapshot = null;
            return null;
        }

        state.Snapshot = snapshot;
        return snapshot;
    }
}
