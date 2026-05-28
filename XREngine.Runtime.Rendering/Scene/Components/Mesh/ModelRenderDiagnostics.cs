using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Components.Scene.Mesh;

internal static class ModelRenderDiagnostics
{
    private const int MaxPublishDetailLines = 128;
    private const int MaxRegistrationLines = 128;
    private const int MaxVisibilityLines = 160;
    private const int MaxCommandLines = 160;
    private const int MaxRejectLines = 160;
    private static readonly TimeSpan TraceDuration = TimeSpan.FromSeconds(30.0);

    private static readonly ConcurrentDictionary<int, long> s_traceUntilTicks = new();
    private static int s_publishDetailLines;
    private static int s_registrationLines;
    private static int s_visibilityLines;
    private static int s_commandLines;
    private static int s_rejectLines;

    private static bool Enabled
    {
        get
        {
#if DEBUG || EDITOR
            return RenderDiagnosticsFlags.ModelRenderDiagEnabled;
#else
            return false;
#endif
        }
    }

    internal static bool HasActiveTrace
    {
        get
        {
            if (!Enabled || s_traceUntilTicks.IsEmpty)
                return false;

            long now = Stopwatch.GetTimestamp();
            foreach (var pair in s_traceUntilTicks)
            {
                if (pair.Value > now)
                    return true;

                s_traceUntilTicks.TryRemove(pair.Key, out _);
            }

            return false;
        }
    }

    internal static void LogComponentPublished(
        ModelComponent component,
        string operation,
        int sourceMeshCount,
        int renderedObjectCount,
        long startTimestamp)
    {
        if (!Enabled)
            return;

        ActivateTrace(component);

        double elapsedMs = ElapsedMs(startTimestamp);
        string componentLabel = ComponentLabel(component);
        Matrix4x4 componentMatrix = component.Transform?.WorldMatrix ?? Matrix4x4.Identity;
        Vector3 translation = componentMatrix.Translation;
        Vector3 scale = ExtractBasisScale(componentMatrix);

        Debug.Meshes(
            "[ModelRenderDiag] Publish op={0} component={1} active={2} world={3} sourceMeshes={4} runtimeMeshes={5} renderedObjects={6} elapsedMs={7:F2} t=({8:F3},{9:F3},{10:F3}) scale=({11:F5},{12:F5},{13:F5})",
            operation,
            componentLabel,
            component.IsActive,
            component.World is null ? "<null>" : component.World.GetType().Name,
            sourceMeshCount,
            component.Meshes.Count,
            renderedObjectCount,
            elapsedMs,
            translation.X,
            translation.Y,
            translation.Z,
            scale.X,
            scale.Y,
            scale.Z);

        int index = 0;
        foreach (RenderableMesh renderable in component.Meshes)
        {
            bool priority = IsPriorityRenderable(renderable);
            if (!priority && Interlocked.Increment(ref s_publishDetailLines) > MaxPublishDetailLines)
            {
                index++;
                continue;
            }

            LogRenderableState(
                ELogCategory.Meshes,
                "[ModelRenderDiag] PublishMesh",
                component,
                renderable,
                index,
                extra: string.Empty);
            index++;
        }
    }

    internal static void LogComponentActivated(ModelComponent component)
    {
        if (!Enabled || component.Model is null)
            return;

        ActivateTrace(component);
        Debug.MeshesEvery(
            $"ModelRenderDiag.Activated.{ComponentKey(component)}",
            TimeSpan.FromSeconds(2.0),
            "[ModelRenderDiag] ComponentActivated component={0} active={1} world={2} runtimeMeshes={3} renderedObjects={4}",
            ComponentLabel(component),
            component.IsActive,
            component.World is null ? "<null>" : component.World.GetType().Name,
            component.Meshes.Count,
            component.RenderedObjects.Length);
    }

    internal static void LogSceneRegistration(
        VisualScene3D scene,
        RenderInfo3D renderInfo,
        bool added,
        bool gpuDispatchActive,
        bool cpuGpuMirrorActive,
        int trackedRenderables)
    {
        if (renderInfo.Owner is not RenderableMesh renderable || !ShouldTrace(renderable))
            return;

        bool priority = IsPriorityRenderable(renderable);
        if (!priority && Interlocked.Increment(ref s_registrationLines) > MaxRegistrationLines)
            return;

        ModelComponent? component = renderable.Component as ModelComponent;
        Debug.Rendering(
            "[ModelRenderDiag] SceneRegistration action={0} scene={1} component={2} mesh={3} worldReg={4} octree={5} gpuDispatch={6} cpuGpuMirror={7} trackedRenderables={8}",
            added ? "add" : "remove",
            scene.GetHashCode(),
            component is null ? "<non-model>" : ComponentLabel(component),
            RenderableLabel(renderable),
            renderInfo.WorldInstance is not null,
            renderInfo.TreeNode is not null,
            gpuDispatchActive,
            cpuGpuMirrorActive,
            trackedRenderables);
    }

    internal static void LogVisibilityAccepted(RenderInfo3D renderInfo, RenderCommandCollection commands, IRuntimeRenderCamera? camera, bool collectMirrors)
    {
        if (renderInfo.Owner is not RenderableMesh renderable || !ShouldTrace(renderable))
            return;

        bool priority = IsPriorityRenderable(renderable);
        if (!priority && Interlocked.Increment(ref s_visibilityLines) > MaxVisibilityLines)
            return;

        ModelComponent? component = renderable.Component as ModelComponent;
        string extra = string.Format(
            "isShadow={0} collectMirrors={1} camera={2} distance={3:F3}",
            commands.IsShadowPass,
            collectMirrors,
            camera?.GetHashCode().ToString() ?? "<null>",
            CameraDistance(renderable, camera));

        LogRenderableState(
            ELogCategory.Rendering,
            "[ModelRenderDiag] VisibilityAccepted",
            component,
            renderable,
            index: -1,
            extra);
    }

    internal static void LogRejected(
        RenderInfo3D renderInfo,
        IVolume? cullingVolume,
        RenderCommandCollection commands,
        IRuntimeCullingCamera? camera,
        bool containsOnly,
        bool collectMirrors)
    {
        if (renderInfo.Owner is not RenderableMesh renderable || !ShouldTrace(renderable))
            return;

        bool priority = IsPriorityRenderable(renderable);
        if (!priority && Interlocked.Increment(ref s_rejectLines) > MaxRejectLines)
            return;

        ModelComponent? component = renderable.Component as ModelComponent;
        string reason = DescribeRejectReason(renderInfo, cullingVolume, commands, camera, containsOnly, collectMirrors);
        LogRenderableState(
            ELogCategory.Rendering,
            "[ModelRenderDiag] VisibilityRejected",
            component,
            renderable,
            index: -1,
            reason);
    }

    internal static void LogCommandCollect(RenderableMesh renderable, RenderCommandMesh3D command, RenderCommandCollection commands, IRuntimeRenderCamera? camera, float distance)
    {
        if (!ShouldTrace(renderable))
            return;

        bool priority = IsPriorityRenderable(renderable);
        if (!priority && Interlocked.Increment(ref s_commandLines) > MaxCommandLines)
            return;

        ModelComponent? component = renderable.Component as ModelComponent;
        XRMeshRenderer? renderer = command.Mesh;
        XRMaterial? material = command.MaterialOverride ?? renderer?.Material;
        XRMesh? mesh = renderer?.Mesh;
        Debug.Rendering(
            "[ModelRenderDiag] CommandCollect component={0} renderable={1} cmd={2} stable={3} sourceSubMesh='{4}' mesh='{5}' material='{6}' pass={7} effectivePass={8} cmdEnabled={9} forceCpu={10} instances={11} isShadow={12} distance={13:F3} cmdDistance={14:F3} sortKey={15} camera={16}",
            component is null ? "<non-model>" : ComponentLabel(component),
            RenderableLabel(renderable),
            RuntimeHelpers.GetHashCode(command),
            command.StableQueryKey,
            renderer?.SourceSubMeshAsset?.Name ?? "<null>",
            mesh?.Name ?? "<null>",
            material?.Name ?? "<null>",
            command.RenderPass,
            material?.RenderPass ?? -1,
            command.Enabled,
            command.ForceCpuRendering,
            command.Instances,
            commands.IsShadowPass,
            distance,
            command.RenderDistance,
            command.SortOrderKey,
            camera?.GetHashCode().ToString() ?? "<null>");
    }

    internal static void LogCollectSummary(
        VisualScene3D scene,
        int trackedRenderables,
        int visibleRenderables,
        int emittedCommands,
        bool gpuCulling,
        IRuntimeRenderCamera? camera,
        bool collectMirrors)
    {
        if (!HasActiveTrace)
            return;

        Debug.RenderingEvery(
            $"ModelRenderDiag.CollectSummary.{scene.GetHashCode()}",
            TimeSpan.FromSeconds(1.0),
            "[ModelRenderDiag] CollectSummary scene={0} trackedRenderables={1} visibleRenderables={2} emittedCommands={3} gpuCulling={4} camera={5} collectMirrors={6}",
            scene.GetHashCode(),
            trackedRenderables,
            visibleRenderables,
            emittedCommands,
            gpuCulling,
            camera?.GetHashCode().ToString() ?? "<null>",
            collectMirrors);
    }

    private static void ActivateTrace(ModelComponent component)
    {
        long until = Stopwatch.GetTimestamp() + (long)(TraceDuration.TotalSeconds * Stopwatch.Frequency);
        s_traceUntilTicks[ComponentKey(component)] = until;
    }

    private static bool ShouldTrace(RenderableMesh renderable)
    {
        if (!Enabled || renderable.Component is not ModelComponent component)
            return false;

        int key = ComponentKey(component);
        long now = Stopwatch.GetTimestamp();
        if (!s_traceUntilTicks.TryGetValue(key, out long until))
            return false;

        if (until > now)
            return true;

        s_traceUntilTicks.TryRemove(key, out _);
        return false;
    }

    private static string DescribeRejectReason(
        RenderInfo3D renderInfo,
        IVolume? cullingVolume,
        RenderCommandCollection commands,
        IRuntimeCullingCamera? camera,
        bool containsOnly,
        bool collectMirrors)
    {
        if (commands.IsShadowPass && !renderInfo.CastsShadows)
            return "reason=shadow-cast-disabled";

        if (!collectMirrors && renderInfo.Owner?.GetType().Name.Contains("Mirror", StringComparison.OrdinalIgnoreCase) == true)
            return "reason=mirror-collection-disabled";

        if (camera is not null && !camera.RendersLayer(renderInfo.Layer))
            return $"reason=layer-mask layer={renderInfo.Layer}";

        Box? worldBox = ((IOctreeItem)renderInfo).WorldCullingVolume;
        if (worldBox is null)
            return "reason=unknown-null-world-box";

        EContainment containment = cullingVolume?.ContainsBox(worldBox.Value) ?? EContainment.Contains;
        if (containsOnly && containment != EContainment.Contains)
            return $"reason=not-contained containment={containment} world={FormatBox(worldBox.Value)}";

        if (!containsOnly && containment == EContainment.Disjoint)
            return $"reason=frustum-disjoint containment={containment} world={FormatBox(worldBox.Value)}";

        return $"reason=unknown containment={containment} world={FormatBox(worldBox.Value)}";
    }

    private static void LogRenderableState(
        ELogCategory category,
        string prefix,
        ModelComponent? component,
        RenderableMesh renderable,
        int index,
        string extra)
    {
        RenderInfo3D renderInfo = renderable.RenderInfo;
        XRMeshRenderer? renderer = renderable.GetCurrentOrFirstLodRenderer();
        XRMesh? mesh = renderer?.Mesh;
        XRMaterial? material = renderer?.Material;
        RenderCommandMesh3D? meshCommand = FindMeshCommand(renderInfo);
        AABB? localBounds = renderInfo.LocalCullingVolume;
        Box? worldBox = ((IOctreeItem)renderInfo).WorldCullingVolume;
        string subMeshName = "<unknown>";
        if (component is not null && component.TryGetSourceSubMesh(renderable, out SubMesh? subMesh))
            subMeshName = subMesh.Name ?? "<unnamed>";

        string message = string.Format(
            "{0} component={1} index={2} subMesh='{3}' renderable={4} mesh='{5}' material='{6}' verts={7} tris={8} skin={9} bones={10} blendShapes={11} pass={12} cmdPass={13} layer={14} riVisible={15} shouldRender={16} worldReg={17} octree={18} local={19} world={20} rootBone='{21}' transparent={22} cull={23} {24}",
            prefix,
            component is null ? "<non-model>" : ComponentLabel(component),
            index,
            subMeshName,
            RenderableLabel(renderable),
            mesh?.Name ?? "<null>",
            material?.Name ?? "<null>",
            mesh?.VertexCount ?? 0,
            mesh?.Triangles?.Count ?? 0,
            renderable.IsSkinned,
            mesh?.UtilizedBones.Length ?? 0,
            mesh?.BlendshapeCount ?? 0,
            material?.RenderPass ?? -1,
            meshCommand?.RenderPass ?? -1,
            renderInfo.Layer,
            renderInfo.IsVisible,
            renderInfo.ShouldRender,
            renderInfo.WorldInstance is not null,
            renderInfo.TreeNode is not null,
            localBounds.HasValue ? FormatAabb(localBounds.Value) : "<null>",
            worldBox.HasValue ? FormatBox(worldBox.Value) : "<null>",
            renderable.RootBone?.Name ?? "<null>",
            material is null ? "<null>" : material.GetEffectiveTransparencyMode().ToString(),
            material?.RenderOptions.CullMode.ToString() ?? "<null>",
            extra);

        Debug.Log(category, EOutputVerbosity.Normal, false, message);
    }

    private static RenderCommandMesh3D? FindMeshCommand(RenderInfo3D renderInfo)
    {
        for (int i = 0; i < renderInfo.RenderCommands.Count; i++)
            if (renderInfo.RenderCommands[i] is RenderCommandMesh3D command)
                return command;

        return null;
    }

    private static bool IsPriorityRenderable(RenderableMesh renderable)
    {
        XRMeshRenderer? renderer = renderable.GetCurrentOrFirstLodRenderer();
        XRMesh? mesh = renderer?.Mesh;
        XRMaterial? material = renderer?.Material;
        return ContainsPriorityToken(mesh?.Name) ||
               ContainsPriorityToken(material?.Name) ||
               (mesh?.VertexCount ?? 0) == 0 ||
               (mesh?.Triangles?.Count ?? 0) == 0 ||
               renderable.RenderInfo.LocalCullingVolume is not { IsValid: true };
    }

    private static bool ContainsPriorityToken(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains("body", StringComparison.OrdinalIgnoreCase);

    private static string ComponentLabel(ModelComponent component)
        => $"'{component.SceneNode?.Name ?? component.Name ?? "<unnamed>"}'#{ComponentKey(component)}";

    private static string RenderableLabel(RenderableMesh renderable)
        => $"#{RuntimeHelpers.GetHashCode(renderable)}";

    private static int ComponentKey(ModelComponent component)
        => RuntimeHelpers.GetHashCode(component);

    private static double CameraDistance(RenderableMesh renderable, IRuntimeRenderCamera? camera)
    {
        if (camera is null)
            return 0.0;

        Vector3 point = renderable.IsSkinned
            ? renderable.RootBone?.RenderTranslation ?? renderable.Component.Transform.RenderTranslation
            : renderable.Component.Transform.RenderTranslation;
        return camera.DistanceFromRenderNearPlane(point);
    }

    private static double ElapsedMs(long startTimestamp)
        => (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

    private static Vector3 ExtractBasisScale(Matrix4x4 matrix)
    {
        Vector3 x = new(matrix.M11, matrix.M12, matrix.M13);
        Vector3 y = new(matrix.M21, matrix.M22, matrix.M23);
        Vector3 z = new(matrix.M31, matrix.M32, matrix.M33);
        return new Vector3(x.Length(), y.Length(), z.Length());
    }

    private static string FormatAabb(AABB bounds)
        => $"center=({bounds.Center.X:F3},{bounds.Center.Y:F3},{bounds.Center.Z:F3}) half=({bounds.HalfExtents.X:F3},{bounds.HalfExtents.Y:F3},{bounds.HalfExtents.Z:F3}) valid={bounds.IsValid}";

    private static string FormatBox(Box box)
        => $"center=({box.LocalCenter.X:F3},{box.LocalCenter.Y:F3},{box.LocalCenter.Z:F3}) half=({box.LocalHalfExtents.X:F3},{box.LocalHalfExtents.Y:F3},{box.LocalHalfExtents.Z:F3})";
}
