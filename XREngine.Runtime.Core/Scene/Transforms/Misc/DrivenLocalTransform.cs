using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Allows code to directly set the local matrix of the scene node.
    /// </summary>
    /// <param name="parent"></param>
    public class DrivenLocalTransform : TransformBase
    {
        public DrivenLocalTransform() { }
        public DrivenLocalTransform(TransformBase parent)
            : base(parent) { }

        private Matrix4x4 _localMatrix;

        public void SetLocalMatrix(Matrix4x4 matrix)
        {
            _localMatrix = matrix;
            MarkLocalModified();
        }
        public void SetWorldMatrix(Matrix4x4 matrix)
        {
            _localMatrix = Parent != null ? Parent.InverseWorldMatrix * matrix : matrix;
            MarkLocalModified();
        }

        protected override Matrix4x4 CreateLocalMatrix() => _localMatrix;
    }
}
