using System.Numerics;
using XREngine.Data.Geometry;

namespace XREngine.Rendering
{
    public class XROrthographicCameraParameters : XRCameraParameters
    {
        private Vector2 _originPercentages = Vector2.Zero;
        private Vector2 _origin;

        public Vector2 Origin => _origin;

        private float _orthoLeft = 0.0f;
        private float _orthoRight = 1.0f;
        private float _orthoBottom = 0.0f;
        private float _orthoTop = 1.0f;

        private float _orthoLeftPercentage = 0.0f;
        private float _orthoRightPercentage = 1.0f;
        private float _orthoBottomPercentage = 0.0f;
        private float _orthoTopPercentage = 1.0f;
        private float _width;
        private float _height;

        public XROrthographicCameraParameters(float width, float height, float nearPlane, float farPlane) : base(nearPlane, farPlane)
        {
            _width = width;
            _height = height;
            Resized();
        }

        public XROrthographicCameraParameters() : this(1.0f, 1.0f, 0.1f, 10000.0f)
        {
        }

        public float Width
        {
            get => _width;
            set => SetField(ref _width, value);
        }

        public float Height 
        {
            get => _height;
            set => SetField(ref _height, value);
        }

        public void Resize(float width, float height)
        {
            _width = width;
            _height = height;
            Resized();
        }

        public void SetOriginCentered()
            => SetOriginPercentages(0.5f, 0.5f);
        public void SetOriginBottomLeft()
            => SetOriginPercentages(0.0f, 0.0f);
        public void SetOriginTopLeft()
            => SetOriginPercentages(0.0f, 1.0f);
        public void SetOriginBottomRight()
            => SetOriginPercentages(1.0f, 0.0f);
        public void SetOriginTopRight()
            => SetOriginPercentages(1.0f, 1.0f);

        public void SetOriginPercentages(Vector2 percentages)
            => SetOriginPercentages(percentages.X, percentages.Y);
        public void SetOriginPercentages(float xPercentage, float yPercentage)
        {
            _originPercentages.X = xPercentage;
            _originPercentages.Y = yPercentage;
            _orthoLeftPercentage = 0.0f - xPercentage;
            _orthoRightPercentage = 1.0f - xPercentage;
            _orthoBottomPercentage = 0.0f - yPercentage;
            _orthoTopPercentage = 1.0f - yPercentage;
            Resized();
        }
        private void Resized()
        {
            _orthoLeft = _orthoLeftPercentage * Width;
            _orthoRight = _orthoRightPercentage * Width;
            _orthoBottom = _orthoBottomPercentage * Height;
            _orthoTop = _orthoTopPercentage * Height;
            _origin = new Vector2(_orthoLeft, _orthoBottom) + _originPercentages * new Vector2(Width, Height);
            ForceInvalidateProjection();
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            switch (propName)
            {
                case nameof(Width):
                case nameof(Height):
                    Resized();
                    break;
            }
        }

        public Vector3 AlignScreenPoint(Vector3 screenPoint)
            => new(screenPoint.X + _orthoLeft, screenPoint.Y + _orthoBottom, screenPoint.Z);
        public Vector3 UnAlignScreenPoint(Vector3 screenPoint)
            => new(screenPoint.X - _orthoLeft, screenPoint.Y - _orthoBottom, screenPoint.Z);

        protected override Matrix4x4 CalculateProjectionMatrix()
            => Matrix4x4.CreateOrthographicOffCenter(_orthoLeft, _orthoRight, _orthoBottom, _orthoTop, NearZ, FarZ);

        protected override Frustum CalculateUntransformedFrustum()
            => new(Width, Height, NearZ, FarZ);

        public override void SetUniforms(XRRenderProgram program)
        {
            //base.SetUniforms(program);
            program.Uniform(EEngineUniform.CameraNearZ.ToString(), NearZ);
            program.Uniform(EEngineUniform.CameraFarZ.ToString(), FarZ);

            program.Uniform(EEngineUniform.ScreenWidth.ToString(), Width);
            program.Uniform(EEngineUniform.ScreenHeight.ToString(), Height);
            program.Uniform(EEngineUniform.ScreenOrigin.ToString(), Origin);
        }

        public BoundingRectangleF GetBounds()
            => new(_orthoLeft, _orthoRight, _orthoBottom, _orthoTop);

        public override Vector2 GetFrustumSizeAtDistance(float drawDistance)
            => new(Width, Height);

        public override string ToString()
            => $"NearZ: {NearZ}, FarZ: {FarZ}, Width: {Width}, Height: {Height}, Origin: {Origin}";
    }
}
