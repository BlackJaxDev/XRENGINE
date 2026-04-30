//using System.ComponentModel;

//namespace XREngine.Components
//{
//    [TFileDef("Landscape Component")]
//    public class LandscapeComponent : CollidableShape3DComponent
//    {
//        public LandscapeComponent()
//        {
//            RenderInfo = new RenderInfo3D(true, false) { CastsShadows = true, ReceivesShadows = true };
//        }

//        private int _stride;
//        private Box _bounds;
//        private IVector2 _dimensions = new IVector2(100, 100);
//        private Vector2 _minMaxHeight = new Vector2(0.0f, 1.0f);
//        private TCollisionHeightField _heightFieldShape;
//        private TCollisionHeightField.EHeightValueType _heightValueType
//            = TCollisionHeightField.EHeightValueType.Single;
//        private Matrix4 _heightOffsetTransform, _heightOffsetTransformInv;

//        [Category("Landscape")]
//        public XRMaterial Material
//        {
//            get => _rc.Mesh?.Material;
//            set
//            {
//                if (_rc.Mesh != null)
//                    _rc.Mesh.Material = value;
//            }
//        }
//        public override IRenderInfo3D RenderInfo
//        {
//            get => base.RenderInfo;
//            protected set
//            {
//                base.RenderInfo = value;
//                RenderInfo.CullingVolume = _bounds;
//            }
//        }
//        [ReadOnly(true)]
//        [Category("Landscape")]
//        public Box Bounds
//        {
//            get => _bounds;
//            private set
//            {
//                _bounds = value;
//                RenderInfo.CullingVolume = _bounds;
//            }
//        }
//        [Browsable(false)]
//        public DataSource DataSource { get; private set; }
//        [Browsable(false)]
//        public Stream Stream { get; private set; }

//        protected override void OnWorldTransformChanged(bool recalcChildWorldTransformsNow = true)
//        {
//            if (Bounds != null)
//                Bounds.Transform.Matrix.Value = WorldMatrix;
//            _rc.WorldMatrix = WorldMatrix;
//            _rc.NormalMatrix = WorldMatrix.Value.GetRotationMatrix3(); //WorldMatrix.Transposed().Inverted().GetRotationMatrix3();
//            base.OnWorldTransformChanged();
//        }

//        public void GenerateHeightFieldCollision(
//            VoidPtr heightDataPtr, int dataLength,
//            int width, int length,
//            float minHeight, float maxHeight,
//            TCollisionHeightField.EHeightValueType valueType,
//            TRigidBodyConstructionInfo bodyInfo)
//            => GenerateHeightFieldCollision(
//                new DataSource(heightDataPtr, dataLength, true), width, length, minHeight, maxHeight, valueType, bodyInfo);

//        public void GenerateHeightFieldCollision(
//            DataSource heightData,
//            int width, int length,
//            float minHeight, float maxHeight,
//            TCollisionHeightField.EHeightValueType valueType,
//            TRigidBodyConstructionInfo bodyInfo)
//        {
//            DataSource = heightData;
//            GenerateHeightFieldCollision(
//                  DataSource.AsStream(), width, length, minHeight, maxHeight, valueType, bodyInfo);
//        }

//        public void GenerateHeightFieldCollision(
//            Stream heightData,
//            int width, int length,
//            float minHeight, float maxHeight,
//            TCollisionHeightField.EHeightValueType valueType,
//            TRigidBodyConstructionInfo bodyInfo)
//        {
//            _dimensions = new IVector2(width, length);
//            _minMaxHeight = new Vector2(minHeight, maxHeight);
//            _heightValueType = valueType;

//            Stream = heightData;

//            float heightScale = 1.0f;
//            _heightFieldShape = TCollisionHeightField.New(_dimensions.X, _dimensions.Y, Stream, heightScale, _minMaxHeight.X, _minMaxHeight.Y, 1, _heightValueType, false);
//            Bounds = new Box(_dimensions.X * 0.5f, (_minMaxHeight.Y - _minMaxHeight.X) * heightScale, _dimensions.Y * 0.5f);
//            Bounds.Transform.Matrix.Value = WorldMatrix;

//            BoundingBox box = _heightFieldShape.GetAabb(Matrix4.Identity);
//            float offset = (_minMaxHeight.X + _minMaxHeight.Y) * 0.5f/* * _heightFieldCollision.LocalScaling.Y*/;
//            _heightOffsetTransform = Matrix4.CreateTranslation(0.0f, offset, 0.0f);
//            _heightOffsetTransformInv = Matrix4.CreateTranslation(0.0f, -offset, 0.0f);

//            if (bodyInfo != null)
//            {
//                bodyInfo.Mass = 0.0f;
//                bodyInfo.LocalInertia = Vector3.Zero;
//                bodyInfo.CollisionShape = _heightFieldShape;
//                bodyInfo.UseMotionState = false;
//                bodyInfo.SleepingEnabled = false;
//                GenerateCollisionObject(bodyInfo);
//            }
//        }

//        public unsafe float GetHeight(int x, int y)
//        {
//            switch (_heightValueType)
//            {
//                case TCollisionHeightField.EHeightValueType.Byte:
//                    {
//                        byte* heightPtr = (byte*)DataSource.Address;
//                        int heightIndex = x + y * _dimensions.X;
//                        if (heightIndex < 0 || heightIndex >= DataSource.Length / sizeof(byte))
//                            throw new IndexOutOfRangeException();
//                        return heightPtr[heightIndex];
//                    }
//                case TCollisionHeightField.EHeightValueType.Int16:
//                    {
//                        short* heightPtr = (short*)DataSource.Address;
//                        int heightIndex = x + y * _dimensions.X;
//                        if (heightIndex < 0 || heightIndex >= DataSource.Length / sizeof(short))
//                            throw new IndexOutOfRangeException();
//                        return heightPtr[heightIndex];
//                    }
//                case TCollisionHeightField.EHeightValueType.Int32:
//                    {
//                        int* heightPtr = (int*)DataSource.Address;
//                        int heightIndex = x + y * _dimensions.X;
//                        if (heightIndex < 0 || heightIndex >= DataSource.Length / sizeof(int))
//                            throw new IndexOutOfRangeException();
//                        return heightPtr[heightIndex];
//                    }
//                default:
//                case TCollisionHeightField.EHeightValueType.FixedPoint88:
//                case TCollisionHeightField.EHeightValueType.Single:
//                    {
//                        float* heightPtr = (float*)DataSource.Address;
//                        int heightIndex = x + y * _dimensions.X;
//                        if (heightIndex < 0 || heightIndex >= DataSource.Length / sizeof(float))
//                            throw new IndexOutOfRangeException();
//                        return heightPtr[heightIndex];
//                    }
//                case TCollisionHeightField.EHeightValueType.Double:
//                    {
//                        double* heightPtr = (double*)DataSource.Address;
//                        int heightIndex = x + y * _dimensions.X;
//                        if (heightIndex < 0 || heightIndex >= DataSource.Length / sizeof(double))
//                            throw new IndexOutOfRangeException();

//                        TODO: support double precision?
//                        return (float)heightPtr[heightIndex];
//                    }
//            }
//        }
//        public unsafe void GenerateHeightFieldMesh(XRMaterial material, int stride = 1)
//        {
//            if (_heightFieldShape is null)
//                throw new InvalidOperationException();

//            _stride = stride;
//            List<TVertexTriangle> list = new List<TVertexTriangle>();
//            TVertex[] vertexNormals = new TVertex[6];

//            int xInc = stride, yInc = stride;
//            int xDim = _dimensions.X - 1, yDim = _dimensions.Y - 1;
//            float uInc = 1.0f / xDim, vInc = 1.0f / yDim;
//            int nextX, nextY, prevX, prevY;
//            int xTriStride = _dimensions.X / xInc * 2;
//            int triCount = 0;

//            float halfX = xDim * 0.5f, halfY = yDim * 0.5f;
//            float yOffset = (_minMaxHeight.X + _minMaxHeight.Y) * 0.5f/* * _heightFieldCollision.LocalScaling.Y*/;

//            for (int thisY = 0; thisY < yDim; thisY += yInc)
//            {
//                nextY = thisY + yInc;
//                prevY = thisY - yInc;

//                for (int thisX = 0; thisX < xDim; thisX += xInc)
//                {
//                    nextX = thisX + xInc;
//                    prevX = thisX - xInc;

//                    Vector3 topLeftPos = GetPosition(thisX, thisY, halfX, halfY, yOffset);
//                    Vector3 topRightPos = GetPosition(nextX, thisY, halfX, halfY, yOffset);
//                    Vector3 bottomLeftPos = GetPosition(thisX, nextY, halfX, halfY, yOffset);
//                    Vector3 bottomRightPos = GetPosition(nextX, nextY, halfX, halfY, yOffset);

//                    Vector3 topLeftNorm = GetSmoothedNormal(thisX, thisY, halfX, halfY, yOffset);
//                    Vector3 bottomLeftNorm = GetSmoothedNormal(thisX, nextY, halfX, halfY, yOffset);
//                    Vector3 bottomRightNorm = GetSmoothedNormal(nextX, nextY, halfX, halfY, yOffset);
//                    Vector3 topRightNorm = GetSmoothedNormal(nextX, thisY, halfX, halfY, yOffset);

//                    Vector2 topLeftUV = new Vector2(thisX * uInc, thisY * vInc);
//                    Vector2 topRightUV = new Vector2(nextX * uInc, thisY * vInc);
//                    Vector2 bottomLeftUV = new Vector2(thisX * uInc, nextY * vInc);
//                    Vector2 bottomRightUV = new Vector2(nextX * uInc, nextY * vInc);

//                    list.Add(new TVertexTriangle(
//                        new TVertex(topLeftPos, topLeftNorm, topLeftUV),
//                        new TVertex(bottomLeftPos, bottomLeftNorm, bottomLeftUV),
//                        new TVertex(bottomRightPos, bottomRightNorm, bottomRightUV)));
//                    list.Add(new TVertexTriangle(
//                        new TVertex(topLeftPos, topLeftNorm, topLeftUV),
//                        new TVertex(bottomRightPos, bottomRightNorm, bottomRightUV),
//                        new TVertex(topRightPos, topRightNorm, topRightUV)));
//                    triCount += 2;
//                }
//            }

//            Rendering.Models.TMesh data = Rendering.Models.TMesh.Create(VertexShaderDesc.PosNormTex(), list);
//            data[EBufferType.Position].MapData = true;
//            data[EBufferType.Normal].MapData = true;
//            data[EBufferType.TexCoord].MapData = true;
//            material.RenderParams.CullMode = ECulling.Back;

//            _rc.Mesh?.Dispose();
//            _rc.Mesh = new MeshRenderer(data, material);
//        }

//        / <summary>
//        / Pushes the updated height mesh to the GPU and updates the collision object.
//        / </summary>
//        public void HeightDataChanged()
//        {
//            if (_heightFieldShape is null)
//                throw new InvalidOperationException();

//            var pos = _rc.Mesh.TargetMesh[EBufferType.Position];
//            var nrm = _rc.Mesh.TargetMesh[EBufferType.Normal];
//            var tex = _rc.Mesh.TargetMesh[EBufferType.TexCoord];

//            TVertex[] vertexNormals = new TVertex[6];

//            int xDim = _dimensions.X - 1, yDim = _dimensions.Y - 1;
//            float uInc = 1.0f / xDim, vInc = 1.0f / yDim;
//            int nextX, nextY, prevX, prevY;
//            int xTriStride = _dimensions.X / _stride * 2;
//            int triCount = 0;

//            float halfX = xDim * 0.5f, halfY = yDim * 0.5f;
//            float yOffset = (_minMaxHeight.X + _minMaxHeight.Y) * 0.5f/* * _heightFieldCollision.LocalScaling.Y*/;

//            int i = 0;
//            for (int thisY = 0; thisY < yDim; thisY += _stride)
//            {
//                nextY = thisY + _stride;
//                prevY = thisY - _stride;

//                for (int thisX = 0; thisX < xDim; thisX += _stride)
//                {
//                    nextX = thisX + _stride;
//                    prevX = thisX - _stride;

//                    Vector3 topLeftPos = GetPosition(thisX, thisY, halfX, halfY, yOffset);
//                    Vector3 topRightPos = GetPosition(nextX, thisY, halfX, halfY, yOffset);
//                    Vector3 bottomLeftPos = GetPosition(thisX, nextY, halfX, halfY, yOffset);
//                    Vector3 bottomRightPos = GetPosition(nextX, nextY, halfX, halfY, yOffset);

//                    Vector3 topLeftNorm = GetSmoothedNormal(thisX, thisY, halfX, halfY, yOffset);
//                    Vector3 bottomLeftNorm = GetSmoothedNormal(thisX, nextY, halfX, halfY, yOffset);
//                    Vector3 bottomRightNorm = GetSmoothedNormal(nextX, nextY, halfX, halfY, yOffset);
//                    Vector3 topRightNorm = GetSmoothedNormal(nextX, thisY, halfX, halfY, yOffset);

//                    Vector2 topLeftUV = new Vector2(thisX * uInc, thisY * vInc);
//                    Vector2 topRightUV = new Vector2(nextX * uInc, thisY * vInc);
//                    Vector2 bottomLeftUV = new Vector2(thisX * uInc, nextY * vInc);
//                    Vector2 bottomRightUV = new Vector2(nextX * uInc, nextY * vInc);

//                    pos.Set(i, topLeftPos);
//                    nrm.Set(i, topLeftNorm);
//                    tex.Set(i, topLeftUV);
//                    ++i;

//                    pos.Set(i, bottomLeftPos);
//                    nrm.Set(i, bottomLeftNorm);
//                    tex.Set(i, bottomLeftUV);
//                    ++i;

//                    pos.Set(i, bottomRightPos);
//                    nrm.Set(i, bottomRightNorm);
//                    tex.Set(i, bottomRightUV);
//                    ++i;

//                    pos.Set(i, topLeftPos);
//                    nrm.Set(i, topLeftNorm);
//                    tex.Set(i, topLeftUV);
//                    ++i;

//                    pos.Set(i, bottomRightPos);
//                    nrm.Set(i, bottomRightNorm);
//                    tex.Set(i, bottomRightUV);
//                    ++i;

//                    pos.Set(i, topRightPos);
//                    nrm.Set(i, topRightNorm);
//                    tex.Set(i, topRightUV);
//                    ++i;

//                    triCount += 2;
//                }
//            }

//            Stream.Dispose();
//            Stream = DataSource.AsStream();

//            CollisionObject.CollisionShape = _heightFieldShape = TCollisionHeightField.New(_dimensions.X, _dimensions.Y, Stream, 1.0f, _minMaxHeight.X, _minMaxHeight.Y, 1, _heightValueType, false);
//            RigidBodyUpdated();
//        }
//        private Vector3 GetPosition(int x, int y, float halfX, float halfY, float yOffset) => new Vector3(x - halfX, GetHeight(x, y) - yOffset, y - halfY);
//        private bool GetQuad(int x, int y, float halfX, float halfY, float yOffset, out Vector3 topLeft, out Vector3 bottomLeft, out Vector3 bottomRight, out Vector3 topRight)
//        {
//            if (x < 0 || y < 0 || x + _stride >= _dimensions.X || y + _stride >= _dimensions.Y)
//            {
//                topLeft = Vector3.Zero;
//                topRight = Vector3.Zero;
//                bottomLeft = Vector3.Zero;
//                bottomRight = Vector3.Zero;
//                return false;
//            }
//            else
//            {
//                topLeft = GetPosition(x, y, halfX, halfY, yOffset);
//                topRight = GetPosition(x + _stride, y, halfX, halfY, yOffset);
//                bottomLeft = GetPosition(x, y + _stride, halfX, halfY, yOffset);
//                bottomRight = GetPosition(x + _stride, y + _stride, halfX, halfY, yOffset);
//                return true;
//            }
//        }
//        private Vector3? GetNormal(int quadX, int quadY, bool tri2, float halfX, float halfY, float yOffset)
//        {
//            if (!GetQuad(quadX, quadY, halfX, halfY, yOffset, out Vector3 topLeft, out Vector3 bottomLeft, out Vector3 bottomRight, out Vector3 topRight))
//                return null;

//            return tri2 ?
//                Vector3.CalculateNormal(topLeft, bottomRight, topRight) :
//                Vector3.CalculateNormal(topLeft, bottomLeft, bottomRight);
//        }
//        private Vector3 GetSmoothedNormal(int x, int y, float halfX, float halfY, float yOffset)
//        {
//            int xPrev = x - _stride;
//            int yPrev = y - _stride;

//            /*
//             ________
//            |\2|\ |\ |
//            |1\|_\|_\|
//            |\ |\ |\ |
//            |_\|_\|_\|
//            |\ |\ |\ |
//            |_\|_\|_\|

//            */

//            Vector3? leftQuadTri2 = GetNormal(xPrev, y, true, halfX, halfY, yOffset);
//            Vector3? topQuadTri1 = GetNormal(x, yPrev, false, halfX, halfY, yOffset);

//            Vector3? thisQuadTri1 = GetNormal(x, y, false, halfX, halfY, yOffset);
//            Vector3? thisQuadTri2 = GetNormal(x, y, true, halfX, halfY, yOffset);

//            Vector3? topLeftQuadTri1 = GetNormal(xPrev, yPrev, false, halfX, halfY, yOffset);
//            Vector3? topLeftQuadTri2 = GetNormal(xPrev, yPrev, true, halfX, halfY, yOffset);

//            Vector3 normal = Vector3.Zero;

//            if (thisQuadTri1.HasValue)
//                normal += thisQuadTri1.Value;
//            if (thisQuadTri2.HasValue)
//                normal += thisQuadTri2.Value;
//            if (leftQuadTri2.HasValue)
//                normal += leftQuadTri2.Value;
//            if (topQuadTri1.HasValue)
//                normal += topQuadTri1.Value;
//            if (topLeftQuadTri1.HasValue)
//                normal += topLeftQuadTri1.Value;
//            if (topLeftQuadTri2.HasValue)
//                normal += topLeftQuadTri2.Value;

//            return normal.NormalizedFast();
//        }
//        public void SaveHeightMap(string path, FREE_IMAGE_FORMAT format, FREE_IMAGE_SAVE_FLAGS flags)
//        {
//            var map = GetHeightmap();
//            map.Save(path, format, flags);
//            map.Dispose();
//        }
//        public unsafe FreeImageBitmap GetHeightmap()
//        {
//            int floatSize = sizeof(float);
//            int size = _dimensions.X * _dimensions.Y * floatSize;
//            byte[] scan0 = new byte[size];
//            int m = 0;
//            for (int h = 0; h < _dimensions.Y; ++h)
//                for (int w = 0; w < _dimensions.X; ++w)
//                {
//                    float height = GetHeight(w, h);
//                    byte[] b = BitConverter.GetBytes(height);
//                    for (int i = 0; i < floatSize; ++i)
//                        scan0[m++] = b[i];
//                }

//            FreeImageBitmap bmp = new FreeImageBitmap(_dimensions.X, _dimensions.Y, 0, 32, FREE_IMAGE_TYPE.FIT_FLOAT, scan0);
//            return bmp;
//        }

//#if EDITOR
//        protected internal override void OnHighlightChanged(bool highlighted)
//        {
//            base.OnHighlightChanged(highlighted);

//            Editor.EditorState.RegisterHighlightedMaterial(_rc.Mesh.Material, highlighted, OwningScene);
//        }
//#endif

//        public override TCollisionShape GetCollisionShape() => _heightFieldShape;

//        [TSerialize("RenderCommand")]
//        private RenderCommandMesh3D _rc = new RenderCommandMesh3D(ERenderPass.OpaqueDeferredLit);
//        protected override RenderCommand3D GetRenderCommand() => _rc;
//    }
//}
