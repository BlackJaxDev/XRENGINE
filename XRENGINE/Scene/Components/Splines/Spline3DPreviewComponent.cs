using Extensions;
using System.Drawing;
using System.Numerics;
using XREngine.Animation;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene
{
    public class Spline3DPreviewComponent : XRComponent, IRenderable
    {
        public RenderInfo3D RenderInfo { get; }

        private readonly RenderCommandMesh3D _rcKfLines = new(EDefaultRenderPass.OpaqueForward);
        private readonly RenderCommandMesh3D _rcCurrentPoint = new(EDefaultRenderPass.OpaqueForward);
        private readonly RenderCommandMesh3D _rcSpline = new(EDefaultRenderPass.OpaqueForward);
        private readonly RenderCommandMesh3D _rcVelocityTangents = new(EDefaultRenderPass.OpaqueForward);
        private readonly RenderCommandMesh3D _rcPoints = new(EDefaultRenderPass.OpaqueForward);
        private readonly RenderCommandMesh3D _rcKeyframeTangents = new(EDefaultRenderPass.OpaqueForward);
        private readonly RenderCommandMesh3D _rcExtrema = new(EDefaultRenderPass.OpaqueForward);

        public bool RenderBounds
        {
            get => RenderInfo.LocalCullingVolume is not null;
            set
            {
                if (value)
                    RenderInfo.LocalCullingVolume = new AABB(Vector3.Zero, Vector3.Zero);
                else
                    RenderInfo.LocalCullingVolume = null;
            }
        }
        public bool RenderSpline
        {
            get => _rcSpline.Enabled;
            set => _rcSpline.Enabled = value;
        }
        public bool RenderTangents
        {
            get => _rcVelocityTangents.Enabled;
            set => _rcVelocityTangents.Enabled = value;
        }
        public bool RenderKeyframeTangentLines
        {
            get => _rcKfLines.Enabled;
            set => _rcKfLines.Enabled = value;
        }
        public bool RenderKeyframeTangentPoints
        {
            get => _rcKeyframeTangents.Enabled;
            set => _rcKeyframeTangents.Enabled = value;
        }
        public bool RenderKeyframePoints
        {
            get => _rcPoints.Enabled;
            set => _rcPoints.Enabled = value;
        }
        public bool RenderCurrentTimePoint
        {
            get => _rcCurrentPoint.Enabled;
            set => _rcCurrentPoint.Enabled = value;
        }
        public bool RenderExtrema
        {
            get => _rcExtrema.Enabled;
            set => _rcExtrema.Enabled = value;
        }

        private PropAnimVector3? _spline = null;
        private XRMeshRenderer? _splinePrimitive;
        private XRMeshRenderer? _velocityTangentsPrimitive;
        private XRMeshRenderer? _pointPrimitive;
        private XRMeshRenderer? _tangentPrimitive;
        private XRMeshRenderer? _keyframeLinesPrimitive;
        private XRMeshRenderer? _timePointPrimitive;
        private XRMeshRenderer? _extremaPrimitive;

        public PropAnimVector3? Spline
        {
            get => _spline;
            set => SetField(ref _spline, value, Spline_Unloaded, Spline_Loaded);
        }
        public RenderInfo[] RenderedObjects { get; }

        private void Spline_Unloaded(PropAnimVector3? spline)
        {
            if (spline is null)
                return;

            spline.Keyframes.Changed -= Keyframes_Changed;
            spline.ConstrainKeyframedFPSChanged -= _position_ConstrainKeyframedFPSChanged;
            spline.BakedFPSChanged -= _position_BakedFPSChanged1;
            spline.LengthChanged -= _position_LengthChanged;
            spline.CurrentPositionChanged -= _position_CurrentPositionChanged;
            //spline.AnimationStarted -= _spline_AnimationStarted;
            //spline.AnimationPaused -= _spline_AnimationEnded;
            //spline.AnimationEnded -= _spline_AnimationEnded;
        }
        private void Spline_Loaded(PropAnimVector3? spline)
        {
            if (spline is null)
                return;

            spline.Keyframes.Changed += Keyframes_Changed;
            spline.ConstrainKeyframedFPSChanged += _position_ConstrainKeyframedFPSChanged;
            spline.BakedFPSChanged += _position_BakedFPSChanged1;
            spline.LengthChanged += _position_LengthChanged;
            spline.CurrentPositionChanged += _position_CurrentPositionChanged;
            //spline.AnimationStarted += _spline_AnimationStarted;
            //spline.AnimationPaused += _spline_AnimationEnded;
            //spline.AnimationEnded += _spline_AnimationEnded;

            //RecalcLocalTransform();
            RegenerateSplinePrimitive();
        }

        private void _position_BakedFPSChanged1(BasePropAnimBakeable obj)
        {
            RegenerateSplinePrimitive();
        }
        private void _position_CurrentPositionChanged(PropAnimVector<Vector3, Vector3Keyframe> obj)
        {
            //RecalcLocalTransform();
            //RegenerateSplinePrimitive();
        }
        private void _position_LengthChanged(BaseAnimation obj)
        {
            RegenerateSplinePrimitive();
        }
        private void _position_ConstrainKeyframedFPSChanged(PropAnimVector<Vector3, Vector3Keyframe> obj)
        {
            RegenerateSplinePrimitive();
        }
        private void Keyframes_Changed(BaseKeyframeTrack obj)
        {
            RegenerateSplinePrimitive();
        }

        public Spline3DPreviewComponent() : this(null) { }
        public Spline3DPreviewComponent(PropAnimVector3? spline) : base()
        {
            Spline = spline;
            RenderInfo = RenderInfo3D.New(this, _rcCurrentPoint, _rcSpline, _rcVelocityTangents, _rcPoints, _rcKeyframeTangents, _rcKfLines, _rcExtrema);
            RenderedObjects = [RenderInfo];
        }

        private Vector3? _cullingVolumeTranslation = null;

        public void RegenerateSplinePrimitive()
        {
            var spline = Spline;

            _splinePrimitive?.Destroy();
            _splinePrimitive = null;
            _velocityTangentsPrimitive?.Destroy();
            _velocityTangentsPrimitive = null;
            _pointPrimitive?.Destroy();
            _pointPrimitive = null;
            _tangentPrimitive?.Destroy();
            _tangentPrimitive = null;
            _timePointPrimitive?.Destroy();
            _timePointPrimitive = null;
            _extremaPrimitive?.Destroy();
            _extremaPrimitive = null;

            if (spline is null || spline.LengthInSeconds <= 0.0f)
                return;

            float fps = spline.ConstrainKeyframedFPS || spline.IsBaked ?
                spline.BakedFramesPerSecond :
                Engine.Time.Timer.TargetRenderFrequency;

            int frameCount = (int)Math.Ceiling(spline.LengthInSeconds * fps) + 1;
            int kfCount = spline.Keyframes.Count << 1;

            List<Vertex> splinePoints = [];
            List<VertexLine> velocity = [];

            Vector3[] keyframePositions = new Vector3[kfCount];
            VertexLine[] keyframeLines = new VertexLine[kfCount];
            Vector3[] tangentPositions = new Vector3[kfCount];

            float velScale = 0.5f;

            int i;
            float sec;
            if (frameCount == 1)
            {
                float density = 0.5f;

                sec = 0.0f;
                while (sec <= spline.LengthInSeconds)
                {
                    AddPoint(spline, splinePoints, velocity, sec, velScale);
                    Vector3 acc = spline.GetAccelerationKeyframed(sec);
                    //adaptive step based on acceleration
                    sec += 1.0f / (1.0f + density * acc.Length());
                    //verify the last point is added
                    if (sec > spline.LengthInSeconds)
                        AddPoint(spline, splinePoints, velocity, spline.LengthInSeconds, velScale);
                }
            }
            else
            {
                float invFps = 1.0f / fps;
                for (i = 0; i < frameCount; ++i)
                {
                    sec = i * invFps;
                    AddPoint(spline, splinePoints, velocity, sec, velScale);
                }
            }

            i = 0;
            Vector3 p0, p1;
            foreach (Vector3Keyframe kf in spline)
            {
                keyframePositions[i] = p0 = kf.InValue;
                tangentPositions[i] = p1 = p0 + kf.InTangent;
                keyframeLines[i] = new VertexLine(p0, p1);
                ++i;

                keyframePositions[i] = p0 = kf.OutValue;
                tangentPositions[i] = p1 = p0 + kf.OutTangent;
                keyframeLines[i] = new VertexLine(p0, p1);
                ++i;
            }

            //Fill the rest in case of non-matching keyframe counts
            while (i < kfCount)
            {
                keyframePositions[i] = p0 = Vector3.Zero;
                tangentPositions[i] = p1 = Vector3.Zero;
                keyframeLines[i] = new VertexLine(p0, p1);
                ++i;
            }

            VertexLineStrip strip = new(false, splinePoints);

            spline.GetMinMax(false,
                out (float Time, float Value)[] min,
                out (float Time, float Value)[] max);

            Vector3[] extrema = new Vector3[6];
            for (int x = 0, index = 0; x < 3; ++x)
            {
                var (TimeMin, ValueMin) = min[x];
                Vector3 minPos = spline.GetValue(TimeMin);
                minPos[x] = ValueMin;

                var (TimeMax, ValueMax) = max[x];
                Vector3 maxPos = spline.GetValue(TimeMax);
                maxPos[x] = ValueMax;

                extrema[index++] = minPos;
                extrema[index++] = maxPos;
            }

            XRMath.ComponentMinMax(out Vector3 minVal, out Vector3 maxVal, extrema);
            var box = new AABB(minVal, maxVal);
            _cullingVolumeTranslation = box.Center;
            RenderInfo.LocalCullingVolume = box.Volume == 0 ? null : box;

            RenderingParameters p = new()
            {
                //LineWidth = 1.0f,
                //PointSize = 5.0f
            };

            XRMesh splineData = XRMesh.Create(strip);
            XRMaterial mat = new(new XRShader(EShaderType.Fragment,
@"
#version 460

layout (location = 0) out vec4 OutColor;
layout (location = 12) in vec4 FragColor0;

void main()
{
    OutColor = FragColor0;
}
"))
            {
                RenderOptions = p
            };
            _splinePrimitive = new XRMeshRenderer(splineData, mat);

            XRMesh velocityData = XRMesh.Create(velocity);
            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Blue);
            mat.RenderOptions = p;
            _velocityTangentsPrimitive = new XRMeshRenderer(velocityData, mat);

            XRMesh pointData = XRMesh.CreatePoints(keyframePositions);
            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Green);
            mat.RenderOptions = p;
            _pointPrimitive = new XRMeshRenderer(pointData, mat);

            XRMesh extremaData = XRMesh.CreatePoints(extrema);
            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Red);
            mat.RenderOptions = p;
            _extremaPrimitive = new XRMeshRenderer(extremaData, mat);

            XRMesh tangentData = XRMesh.CreateLines(tangentPositions);
            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Purple);
            mat.RenderOptions = p;
            _tangentPrimitive = new XRMeshRenderer(tangentData, mat);
            
            XRMesh kfLineData = XRMesh.Create(keyframeLines);
            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Orange);
            mat.RenderOptions = p;
            _keyframeLinesPrimitive = new XRMeshRenderer(kfLineData, mat);

            XRMesh timePointData = XRMesh.CreatePoints(Vector3.Zero);
            mat = XRMaterial.CreateUnlitColorMaterialForward(Color.White);
            mat.RenderOptions = p;
            _timePointPrimitive = new XRMeshRenderer(timePointData, mat);

            _rcVelocityTangents.Mesh = _velocityTangentsPrimitive;
            _rcPoints.Mesh = _pointPrimitive;
            _rcKeyframeTangents.Mesh = _tangentPrimitive;
            _rcSpline.Mesh = _splinePrimitive;
            _rcKfLines.Mesh = _keyframeLinesPrimitive;
            _rcCurrentPoint.Mesh = _timePointPrimitive;
            _rcExtrema.Mesh = _extremaPrimitive;

            RenderTangents = false;
        }

        private static void AddPoint(PropAnimVector3 spline, List<Vertex> splinePoints, List<VertexLine> velocity, float sec, float velocityScale = 1.0f)
        {
            Vector3 val = spline.GetValue(sec);
            Vector3 vel = spline.GetVelocityKeyframed(sec);
            float velLength = vel.Length();
            float t = 1.0f / (1.0f + 0.5f * (velLength * velLength));
            Vertex pos = new(val);
            pos.ColorSets.Add(new Vector4(Vector3.Lerp(Vector3.UnitZ, Vector3.UnitX, t), 1.0f));
            splinePoints.Add(pos);
            velocity.Add(new VertexLine(pos, new Vertex(pos.Position + vel.Normalized() * velocityScale)));
        }

        private Matrix4x4 _localTRS = Matrix4x4.Identity;

        //protected override void OnRecalcLocalTransform(out Matrix4x4 localTransform, out Matrix4x4 inverseLocalTransform)
        //{
        //    base.OnRecalcLocalTransform(out localTransform, out inverseLocalTransform);

        //    Matrix4x4 splinePosMtx, invSplinePosMtx;
        //    var spline = _spline;
        //    if (spline is not null)
        //    {
        //        var pos = spline.CurrentPosition;
        //        splinePosMtx = Matrix4x4.CreateTranslation(pos);
        //        invSplinePosMtx = Matrix4x4.CreateTranslation(-pos);
        //    }
        //    else
        //    {
        //        splinePosMtx = Matrix4x4.Identity;
        //        invSplinePosMtx = Matrix4x4.Identity;
        //    }

        //    _localTRS = localTransform;

        //    localTransform = _localTRS * splinePosMtx;
        //    inverseLocalTransform = invSplinePosMtx * inverseLocalTransform;
        //}
        //protected override void DeriveMatrix()
        //{
        //    _localTRS.DeriveTRS(out Vector3 t, out Vector3 s, out Quat r);
        //    _translation.SetValueSilent(t);
        //    _scale.SetValueSilent(s);
        //    _rotation.SetRotationsNoUpdate(r.ToRotator());
        //}
        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);

            _rcKfLines.WorldMatrix = renderMatrix;
            _rcSpline.WorldMatrix = renderMatrix;
            _rcVelocityTangents.WorldMatrix = renderMatrix;
            _rcPoints.WorldMatrix = renderMatrix;
            _rcKeyframeTangents.WorldMatrix = renderMatrix;
            _rcExtrema.WorldMatrix = renderMatrix;
            _rcCurrentPoint.WorldMatrix = renderMatrix;

            //RenderInfo.LocalCullingVolume?.SetTransformMatrix(mtx * _cullingVolumeTranslation.AsTranslationMatrix());
        }
    }
}