using System.Numerics;
using System.Runtime.InteropServices.JavaScript;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Builds and validates a real engine scene through the browser-owned frame loop.</summary>
public static partial class SceneBoot
{
    private static RuntimeSceneHost? _host;
    private static SceneNode? _child;
    private static SceneBootComponent? _component;
    private static int _initialObjectCount;

    [JSExport]
    public static void Start()
    {
        Shutdown();
        _initialObjectCount = Data.Core.XRObjectBase.ObjectsCache.Count;
        _host = new RuntimeSceneHost();
        try
        {
            var parent = new SceneNode("MovingParent", new Transform(new Vector3(1, 0, 0)));
            _host.RootNodes.Add(parent);
            _child = new SceneNode(parent, "LifecycleProbe", new Transform(new Vector3(0, 1, 0)));
            _component = parent.AddComponent(static () => new SceneBootComponent())
                ?? throw new InvalidOperationException("Scene component creation failed.");
            _host.Start();
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    [JSExport]
    public static int Step()
    {
        RuntimeSceneHost host = _host ?? throw new InvalidOperationException("Scene has not started.");
        if (host.StepCount < 120)
            host.Advance(1.0f / 60.0f);
        Data.Core.XRObjectBase.ProcessPendingDestructions();
        return (int)host.StepCount;
    }

    [JSExport]
    public static string Complete()
    {
        RuntimeSceneHost host = _host ?? throw new InvalidOperationException("Scene has not started.");
        SceneBootComponent component = _component ?? throw new InvalidOperationException("Scene component is missing.");
        Vector3 position = _child!.Transform.WorldTranslation;
        if (host.StepCount != 120 || component.Begins != 1 || component.Updates != 120 ||
            Vector3.Distance(position, new Vector3(2, 1, 0)) > 0.0001f)
            throw new InvalidOperationException($"Scene mismatch: steps={host.StepCount}, begin={component.Begins}, updates={component.Updates}, position={position}.");

        host.Stop();
        host.Advance(1.0f / 60.0f);
        if (component.Ends != 1 || component.Updates != 120)
            throw new InvalidOperationException("Scene continued updating after stop or ended incorrectly.");
        Shutdown();
        if (Data.Core.XRObjectBase.ObjectsCache.Count != _initialObjectCount)
            throw new InvalidOperationException($"Scene teardown left registered engine objects: initial={_initialObjectCount}, remaining={string.Join(", ", Data.Core.XRObjectBase.ObjectsCache.Values.Select(static obj => $"{obj.GetType().Name}:{obj.Name}"))}.");
        return "PASS: real SceneNode + Transform + XRComponent; 120 fixed updates; child=(2, 1, 0); begin/end once; no updates after stop; object cache restored. GPU rendering is not exercised by this fixture.";
    }

    [JSExport]
    public static void Shutdown()
    {
        _host?.Dispose();
        // Nodes defer component/transform destruction to the application frame owner.
        Data.Core.XRObjectBase.ProcessPendingDestructions();
        _host = null;
        _child = null;
        _component = null;
    }
}

