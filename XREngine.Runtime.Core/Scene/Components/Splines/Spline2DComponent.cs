//using System.Drawing;
//using System.Numerics;
//using XREngine.Animation;
//using XREngine.Rendering;
//using XREngine.Rendering.Models.Materials;

//namespace XREngine.Components.Scene
//{
//    public class Spline2DComponent : TransformComponent, I3DRenderable
//    {
//        public IRenderInfo3D RenderInfo { get; } = new RenderInfo3D(true, true) { CastsShadows = false, ReceivesShadows = false };
        
//        [TSerialize]
//        public bool RenderBounds { get; set; } = true;
//        [TSerialize]
//        public bool RenderSpline { get; set; } = true;
//        [TSerialize]
//        public bool RenderTangents { get; set; } = false;
//        [TSerialize]
//        public bool RenderKeyframeTangentLines { get; set; } = true;
//        [TSerialize]
//        public bool RenderKeyframeTangentPoints { get; set; } = true;
//        [TSerialize]
//        public bool RenderKeyframePoints { get; set; } = true;
//        [TSerialize]
//        public bool RenderCurrentTimePoint { get; set; } = true;
//        [TSerialize]
//        public bool RenderExtrema { get; set; } = true;
        
//        private PropAnimVector2 _spline;
//        private XRMeshRenderer _splinePrimitive;
//        private XRMeshRenderer _velocityTangentsPrimitive;
//        private XRMeshRenderer _pointPrimitive;
//        private XRMeshRenderer _tangentPrimitive;
//        private XRMeshRenderer _keyframeLinesPrimitive;
//        private XRMeshRenderer _timePointPrimitive;
//        private XRMeshRenderer _extremaPrimitive;

//        [TSerialize]
//        public PropAnimVector2 Spline
//        {
//            get => _spline;
//            set
//            {
//                if (_spline != null)
//                {
//                    _spline.Keyframes.Changed -= Keyframes_Changed;
//                    _spline.ConstrainKeyframedFPSChanged -= _position_ConstrainKeyframedFPSChanged;
//                    _spline.BakedFPSChanged -= _position_BakedFPSChanged1;
//                    _spline.LengthChanged -= _position_LengthChanged;
//                    //_position.AnimationStarted -= _spline_AnimationStarted;
//                    //_position.AnimationPaused -= _spline_AnimationEnded;
//                    //_position.AnimationEnded -= _spline_AnimationEnded;
//                    _spline.CurrentPositionChanged -= _position_CurrentPositionChanged;
//                    //_spline_AnimationEnded();
//                }
//                _spline = value;
//                if (_spline != null)
//                {
//                    _spline.Keyframes.Changed += Keyframes_Changed;
//                    _spline.ConstrainKeyframedFPSChanged += _position_ConstrainKeyframedFPSChanged;
//                    _spline.BakedFPSChanged += _position_BakedFPSChanged1;
//                    _spline.LengthChanged += _position_LengthChanged;
//                    //_position.AnimationStarted += _spline_AnimationStarted;
//                    //_position.AnimationPaused += _spline_AnimationEnded;
//                    //_position.AnimationEnded += _spline_AnimationEnded;
//                    _spline.CurrentPositionChanged += _position_CurrentPositionChanged;
//                    _spline.TickSelf = true;
//                    //if (_position.State == EAnimationState.Playing)
//                    //    _spline_AnimationStarted();
//                }
//                RegenerateSplinePrimitive();
//            }
//        }

//        private void _position_BakedFPSChanged1(BasePropAnimBakeable obj)
//        {
//            RegenerateSplinePrimitive();
//        }
//        private void _position_CurrentPositionChanged(PropAnimVector<Vector2, Vector2Keyframe> obj)
//        {
//            RecalcLocalTransform();
//            //RegenerateSplinePrimitive();
//        }
//        private void _position_LengthChanged(BaseAnimation obj)
//        {
//            RegenerateSplinePrimitive();
//        }
//        private void _position_ConstrainKeyframedFPSChanged(PropAnimVector<Vector2, Vector2Keyframe> obj)
//        {
//            RegenerateSplinePrimitive();
//        }
//        private void Keyframes_Changed(BaseKeyframeTrack obj)
//        {
//            RegenerateSplinePrimitive();
//        }
        
//        public Spline2DComponent() : this(null) { }
//        public Spline2DComponent(PropAnimVector2 spline) : base() => Spline = spline;
        
//        EventVector3 _cullingVolumeTranslation = null;
//        public void RegenerateSplinePrimitive()
//        {
//            _splinePrimitive?.Dispose();
//            _splinePrimitive = null;
//            _velocityTangentsPrimitive?.Dispose();
//            _velocityTangentsPrimitive = null;
//            _pointPrimitive?.Dispose();
//            _pointPrimitive = null;
//            _tangentPrimitive?.Dispose();
//            _tangentPrimitive = null;
//            _timePointPrimitive?.Dispose();
//            _timePointPrimitive = null;
//            _extremaPrimitive?.Dispose();
//            _extremaPrimitive = null;

//            if (_spline is null || _spline.LengthInSeconds <= 0.0f)
//                return;

//            //TODO: when the FPS is unconstrained, use adaptive vertex points based on velocity/acceleration
//            float fps = _spline.ConstrainKeyframedFPS || _spline.IsBaked ?
//                _spline.BakedFramesPerSecond :
//                (Engine.TargetFramesPerSecond == 0 ? 30.0f : Engine.TargetFramesPerSecond);

//            int frameCount = (int)Math.Ceiling(_spline.LengthInSeconds * fps) + 1;
//            float invFps = 1.0f / fps;
//            int kfCount = _spline.Keyframes.Count << 1;

//            TVertex[] splinePoints = new TVertex[frameCount];
//            TVertexLine[] velocity = new TVertexLine[frameCount];
//            Vector3[] keyframePositions = new Vector3[kfCount];
//            TVertexLine[] keyframeLines = new TVertexLine[kfCount];
//            Vector3[] tangentPositions = new Vector3[kfCount];

//            int i;
//            float sec;
//            for (i = 0; i < splinePoints.Length; ++i)
//            {
//                sec = i * invFps;
//                Vector3 val = _spline.GetValueKeyframed(sec);
//                Vector3 vel = _spline.GetVelocityKeyframed(sec);
//                float velLength = vel.LengthFast;
//                Vector3 velColor = Vector3.Lerp(Vector3.UnitZ, Vector3.UnitX, 1.0f / (1.0f + 0.1f * (velLength * velLength)));
//                TVertex pos = new TVertex(val) { Color = velColor };
//                splinePoints[i] = pos;
//                velocity[i] = new TVertexLine(pos, new TVertex(pos.Position + vel.Normalized()));
//            }
//            i = 0;
//            Vector3 p0, p1;
//            foreach (Vector2Keyframe kf in _spline)
//            {
//                keyframePositions[i] = p0 = kf.InValue;
//                tangentPositions[i] = p1 = p0 + kf.InTangent;
//                keyframeLines[i] = new TVertexLine(p0, p1);
//                ++i;

//                keyframePositions[i] = p0 = kf.OutValue;
//                tangentPositions[i] = p1 = p0 + kf.OutTangent;
//                keyframeLines[i] = new TVertexLine(p0, p1);
//                ++i;
//            }
//            //Fill the rest in case of non-matching keyframe counts
//            while (i < kfCount)
//            {
//                keyframePositions[i] = p0 = Vector3.Zero;
//                tangentPositions[i] = p1 = Vector3.Zero;
//                keyframeLines[i] = new TVertexLine(p0, p1);
//                ++i;
//            }

//            VertexLineStrip strip = new VertexLineStrip(false, splinePoints);

//            _spline.GetMinMax(false,
//                out (float Time, float Value)[] min, 
//                out (float Time, float Value)[] max);

//            Vector3[] extrema = new Vector3[6];
//            for (int x = 0, index = 0; x < 3; ++x)
//            {
//                var (TimeMin, ValueMin) = min[x];
//                Vector3 minPos = _spline.GetValue(TimeMin);
//                minPos[x] = ValueMin;
                
//                var (TimeMax, ValueMax) = max[x];
//                Vector3 maxPos = _spline.GetValue(TimeMax);
//                maxPos[x] = ValueMax;
                
//                extrema[index++] = minPos;
//                extrema[index++] = maxPos;
//            }

//            TMath.ComponentMinMax(out Vector3 minVal, out Vector3 maxVal, extrema);
//            var box = BoundingBox.FromMinMax(minVal, maxVal);
//            _cullingVolumeTranslation = box.Translation;
//            RenderInfo.CullingVolume = box;

//            RenderingParameters p = new RenderingParameters
//            {
//                LineWidth = 1.0f,
//                PointSize = 5.0f
//            };

//            Rendering.Models.TMesh splineData = Rendering.Models.TMesh.Create(XRMeshDescriptor.PosColor(), strip);
//            XRMaterial mat = new XRMaterial("SplineColor", new XRShader(EShaderType.Fragment,
//@"
//#version 450

//layout (location = 0) out Vector4 OutColor;
//layout (location = 4) in Vector4 FragColor0;

//void main()
//{
//    OutColor = FragColor0;
//}
//"))
//            {
//                RenderParams = p
//            };
//            _splinePrimitive = new MeshRenderer(splineData, mat);

//            Rendering.Models.TMesh velocityData = Rendering.Models.TMesh.Create(XRMeshDescriptor.JustPositions(), velocity);
//            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Blue);
//            mat.RenderParams = p;
//            _velocityTangentsPrimitive = new MeshRenderer(velocityData, mat);

//            Rendering.Models.TMesh pointData = Rendering.Models.TMesh.Create(keyframePositions);
//            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Green);
//            mat.RenderParams = p;
//            _pointPrimitive = new MeshRenderer(pointData, mat);

//            Rendering.Models.TMesh extremaData = Rendering.Models.TMesh.Create(extrema);
//            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Red);
//            mat.RenderParams = p;
//            _extremaPrimitive = new MeshRenderer(extremaData, mat);

//            Rendering.Models.TMesh tangentData = Rendering.Models.TMesh.Create(tangentPositions);
//            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Purple);
//            mat.RenderParams = p;
//            _tangentPrimitive = new MeshRenderer(tangentData, mat);

//            Rendering.Models.TMesh kfLineData = Rendering.Models.TMesh.Create(XRMeshDescriptor.JustPositions(), keyframeLines);
//            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Orange);
//            mat.RenderParams = p;
//            _keyframeLinesPrimitive = new MeshRenderer(kfLineData, mat);

//            Rendering.Models.TMesh timePointData = Rendering.Models.TMesh.Create(Vector3.Zero);
//            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.White);
//            mat.RenderParams = p;
//            _timePointPrimitive = new MeshRenderer(timePointData, mat);

//            _rcVelocityTangents.Mesh = _velocityTangentsPrimitive;
//            _rcPoints.Mesh = _pointPrimitive;
//            _rcKeyframeTangents.Mesh = _tangentPrimitive;
//            _rcSpline.Mesh = _splinePrimitive;
//            _rcKfLines.Mesh = _keyframeLinesPrimitive;
//            _rcCurrentPoint.Mesh = _timePointPrimitive;
//            _rcExtrema.Mesh = _extremaPrimitive;
//        }
//        private Matrix4 _localTRS = Matrix4.Identity;
//        protected override void OnRecalcLocalTransform(out Matrix4 localTransform, out Matrix4 inverseLocalTransform)
//        {
//            base.OnRecalcLocalTransform(out localTransform, out inverseLocalTransform);

//            Matrix4 splinePosMtx, invSplinePosMtx;
//            if (_spline != null)
//            {
//                splinePosMtx = Matrix4.CreateTranslation(_spline.CurrentPosition);
//                invSplinePosMtx = Matrix4.CreateTranslation(-_spline.CurrentPosition);
//            }
//            else
//            {
//                splinePosMtx = Matrix4.Identity;
//                invSplinePosMtx = Matrix4.Identity;
//            }

//            _localTRS = localTransform;

//            localTransform = _localTRS * splinePosMtx;
//            inverseLocalTransform = invSplinePosMtx * inverseLocalTransform;
//        }
//        //protected override void DeriveMatrix()
//        //{
//        //    _localTRS.DeriveTRS(out Vector3 t, out Vector3 s, out Quat r);
//        //    _translation.Value = t;
//        //    _scale.Value = s;
//        //    _rotation.SetRotations(r.ToRotator());
//        //}
//        protected override void OnWorldTransformChanged(bool recalcChildWorldTransformsNow = true)
//        {
//            Matrix4 mtx = ParentWorldMatrix * _localTRS;
//            _rcKfLines.WorldMatrix = mtx;
//            _rcSpline.WorldMatrix = mtx;
//            _rcVelocityTangents.WorldMatrix = mtx;
//            _rcPoints.WorldMatrix = mtx;
//            _rcKeyframeTangents.WorldMatrix = mtx;
//            _rcExtrema.WorldMatrix = mtx;
//            _rcCurrentPoint.WorldMatrix = WorldMatrix;

//            RenderInfo.CullingVolume?.SetTransformMatrix(mtx * _cullingVolumeTranslation.AsTranslationMatrix());

//            base.OnWorldTransformChanged(recalcChildWorldTransformsNow);
//        }
        
//        private readonly RenderCommandMesh3D _rcKfLines = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        private readonly RenderCommandMesh3D _rcCurrentPoint = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        private readonly RenderCommandMesh3D _rcSpline = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        private readonly RenderCommandMesh3D _rcVelocityTangents = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        private readonly RenderCommandMesh3D _rcPoints = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        private readonly RenderCommandMesh3D _rcKeyframeTangents = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        private readonly RenderCommandMesh3D _rcExtrema = new RenderCommandMesh3D(ERenderPass.OpaqueForward);
//        public void AddRenderables(RenderPasses passes, ICamera camera)
//        {
//            if (_spline is null)
//                return;

//            if (RenderSpline)
//                passes.Add(_rcSpline);
//            if (RenderTangents)
//                passes.Add(_rcVelocityTangents);
//            if (RenderKeyframePoints)
//                passes.Add(_rcPoints);
//            if (RenderKeyframeTangentPoints)
//                passes.Add(_rcKeyframeTangents);
//            if (RenderKeyframeTangentLines)
//                passes.Add(_rcKfLines);
//            if (RenderExtrema)
//                passes.Add(_rcExtrema);
//            //if (RenderBounds)
//            //    RenderInfo.CullingVolume?.AddRenderables(passes, camera);
//            if (RenderCurrentTimePoint)
//                passes.Add(_rcCurrentPoint);
//        }
//    }
//}