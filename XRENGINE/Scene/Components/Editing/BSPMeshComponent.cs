using System.ComponentModel;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Data.BSP;
using XREngine.Data.Geometry;
using XREngine.Data.Lists;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Scene.Components.Editing
{
    public class BSPMeshComponent : RenderableComponent
    {
        private XRMesh? _baseMesh;
        private XRMaterial? _material;

        private readonly EventList<BSPMeshModifier> _modifiers = new() { ThreadSafe = true };

        [Category("BSP")]
        public XRMesh? BaseMesh
        {
            get => _baseMesh;
            set => SetField(ref _baseMesh, value);
        }

        [Category("BSP")]
        public EventList<BSPMeshModifier> Modifiers => _modifiers;

        [Category("BSP")]
        public XRMaterial? Material
        {
            get => _material;
            set => SetField(ref _material, value);
        }

        public BSPMeshComponent()
        {
            _modifiers.PostAnythingAdded += Modifiers_PostAnythingAdded;
            _modifiers.PostAnythingRemoved += Modifiers_PostAnythingRemoved;
        }

        private void Modifiers_PostAnythingAdded(BSPMeshModifier item)
        {
            item.PropertyChanged += Modifier_PropertyChanged;
            Rebuild();
        }

        private void Modifiers_PostAnythingRemoved(BSPMeshModifier item)
        {
            item.PropertyChanged -= Modifier_PropertyChanged;
            Rebuild();
        }

        private void Modifier_PropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            // Any modifier change affects the final output.
            Rebuild();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(BaseMesh):
                case nameof(Material):
                    Rebuild();
                    break;
            }
        }

        private void Rebuild()
        {
            Meshes.Clear();

            if (BaseMesh is null)
                return;

            // Start with base mesh triangles.
            List<Triangle> current = ToTriangles(BaseMesh);

            // Apply modifiers sequentially: current op modifierMesh
            for (int i = 0; i < _modifiers.Count; i++)
            {
                var mod = _modifiers[i];
                if (mod.Mesh is null)
                    continue;

                BSPNode a = new();
                a.Build(current);

                BSPNode b = new();
                b.Build(ToTriangles(mod.Mesh));

                current = mod.Operation switch
                {
                    EIntersectionType.Union => BSPBoolean.Union(a, b),
                    EIntersectionType.Intersection => BSPBoolean.Intersect(a, b),
                    EIntersectionType.Subtraction => BSPBoolean.Subtract(a, b),
                    EIntersectionType.Merge => BSPBoolean.Union(a, b),
                    _ => BSPBoolean.Union(a, b),
                };
            }

            XRMesh resultMesh = ToXRMesh(current);
            Meshes.Add(new RenderableMesh(new SubMesh(resultMesh, Material), this));
        }

        private static List<Triangle> ToTriangles(XRMesh mesh)
        {
            if (mesh.Triangles is null || mesh.Triangles.Count == 0)
                return [];

            List<Triangle> triangles = new(mesh.Triangles.Count);
            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                var idx = mesh.Triangles[i];
                Vector3 a = mesh.GetPosition((uint)idx.Point0);
                Vector3 b = mesh.GetPosition((uint)idx.Point1);
                Vector3 c = mesh.GetPosition((uint)idx.Point2);
                triangles.Add(new Triangle(a, b, c));
            }
            return triangles;
        }

        private static XRMesh ToXRMesh(List<Triangle> triangles)
        {
            if (triangles.Count == 0)
                return new XRMesh([]);

            List<VertexTriangle> prims = new(triangles.Count);
            for (int i = 0; i < triangles.Count; i++)
            {
                Triangle t = triangles[i];
                Vector3 n = t.GetNormal();
                prims.Add(new VertexTriangle(new Vertex(t.A, n), new Vertex(t.B, n), new Vertex(t.C, n)));
            }
            return new XRMesh(prims);
        }
    }

    public class BSPMeshModifier : XRBase
    {
        private XRMesh? _mesh;
        private EIntersectionType _operation = EIntersectionType.Union;

        [Category("BSP")]
        public XRMesh? Mesh
        {
            get => _mesh;
            set => SetField(ref _mesh, value);
        }

        [Category("BSP")]
        public EIntersectionType Operation
        {
            get => _operation;
            set => SetField(ref _operation, value);
        }
    }

    public enum EIntersectionType
    {
        Union,
        Intersection,
        Subtraction,
        Merge,
        Attach,
        Insert,
    }
}
