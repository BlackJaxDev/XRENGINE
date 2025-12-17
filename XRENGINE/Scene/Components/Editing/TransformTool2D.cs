//using XREngine.Rendering.Cameras;
//using XREngine.Rendering.UI;
//using System;
//using System.Collections.Generic;
//using XREngine.Core.Shapes;
//using XREngine.Components.Scene.Transforms;
//using XREngine.Components.Scene.Mesh;
//using XREngine.Core.Maths.Transforms;

//namespace XREngine.Actors.Types
//{
//    public class TransformTool2D : UIComponent
//    {
//        public TransformTool2D(UIComponent modified)
//        {
//            _modified = modified;
//        }
        
//        private void _transform_WorldTransformChanged()
//        {
//            _transformChanged = true;
//        }

//        private bool _transformChanged = false;
//        private TransformType _mode = TransformType.Translate;
//        private ISocket _modified = null;
//        private TransformComponent _transform;

//        public TransformType Mode
//        {
//            get => _mode;
//            set => _mode = value;
//        }
//        public ISocket ModifiedComponent
//        {
//            get => _modified;
//            set => _modified = value;
//        }

//        public static TransformTool2D CurrentInstance;

//        private bool _hiX, _hiY, _hiZ, _hiCirc, _hiSphere;
//        private const float _orbRadius = 1.0f;
//        private const float _circRadius = 1.2f;
//        private const float _axisSnapRange = 7.0f;
//        private const float _selectRange = 0.03f; //Selection error range for orb and circ
//        private const float _axisSelectRange = 0.15f; //Selection error range for axes
//        private const float _selectOrbScale = _selectRange / _orbRadius;
//        private const float _circOrbScale = _circRadius / _orbRadius;
//        private const float _axisLDist = 2.0f;
//        private const float _axisHalfLDist = 0.75f;
//        private const float _apthm = 0.075f;
//        private const float _dst = 1.5f;
//        private const float _scaleHalf1LDist = 0.8f;
//        private const float _scaleHalf2LDist = 1.2f;

//        protected override void OnResizeLayout(BoundingRectangleF parentRegion) => throw new NotImplementedException();

//        public bool UpdateCursorRay(Ray cursor, Camera camera, bool pressed)
//        {
//            bool clamp = true, snapFound = false;
//            Ray localRay = cursor.TransformedBy(_transform.InverseWorldMatrix);
//            float radius = camera.DistanceScale(_transform.Translation, 1.0f);
//            if (_mode == TransformType.Rotate)
//            {
//                if (!localRay.LineSphereIntersect(Vector3.Zero, radius, out Vector3 point))
//                {
//                    //If no intersect is found, project the ray through the plane perpendicular to the camera.
//                    localRay.LinePlaneIntersect(Vector3.Zero, (camera.WorldPoint - _transform.Translation).Normalized(), out point);

//                    //Clamp the point to edge of the sphere
//                    if (clamp)
//                        point = Ray.PointAtLineDistance(_transform.Translation, point, radius);
//                }

//                float distance = point.LengthFast;

//                //Point lies within orb radius?
//                if (Math.Abs(distance - radius) < (radius * _selectOrbScale))
//                {
//                    _hiSphere = true;

//                    //Determine axis snapping
//                    Vector3 angles = XREngine.Core.Maths.TMath.RadToDeg(point.GetAngles());
//                    angles.X = Math.Abs(angles.X);
//                    angles.Y = Math.Abs(angles.Y);
//                    angles.Z = Math.Abs(angles.Z);

//                    if (Math.Abs(angles.Y - 90.0f) <= _axisSnapRange)
//                        _hiX = true;
//                    else if (angles.X >= (180.0f - _axisSnapRange) || angles.X <= _axisSnapRange)
//                        _hiY = true;
//                    else if (angles.Y >= (180.0f - _axisSnapRange) || angles.Y <= _axisSnapRange)
//                        _hiZ = true;
//                }
//                //Point lies on circ line?
//                else if (Math.Abs(distance - (radius * _circOrbScale)) < (radius * _selectOrbScale))
//                    _hiCirc = true;

//                if (_hiX || _hiY || _hiZ || _hiCirc)
//                    snapFound = true;
//            }
//            else
//            {
//                Plane yz = new Plane(Vector3.Zero, localRay.StartPoint.X < 0.0f ? -Vector3.Right  : Vector3.Right);
//                Plane xz = new Plane(Vector3.Zero, localRay.StartPoint.Y < 0.0f ? -Vector3.Up     : Vector3.Up);
//                Plane xy = new Plane(Vector3.Zero, localRay.StartPoint.Z < 0.0f ? Vector3.Forward : -Vector3.Forward);

//                //if (Collision.PlaneIntersectsPoint(_xz, cursor.StartPoint) == EPlaneIntersection.Back)

//                Vector3[] intersectionPoints = new Vector3[3];
//                bool[] intersects = new bool[3]
//                {
//                    localRay.LinePlaneIntersect(yz, out intersectionPoints[0]),
//                    localRay.LinePlaneIntersect(xz, out intersectionPoints[1]),
//                    localRay.LinePlaneIntersect(xy, out intersectionPoints[2]),
//                };
//                List<Vector3> testDiffs = new List<Vector3>();
//                for (int i = 0; i < 3; ++i)
//                {
//                    if (!intersects[i])
//                        continue;
//                    Vector3 diff = intersectionPoints[i] / radius;
//                    if (diff.X > -_axisSelectRange && diff.X < (_axisLDist + 0.01f) &&
//                        diff.Y > -_axisSelectRange && diff.Y < (_axisLDist + 0.01f) &&
//                        diff.Z > -_axisSelectRange && diff.Z < (_axisLDist + 0.01f))
//                        testDiffs.Add(diff);
//                }

//                //Check if point lies on a specific axis
//                foreach (Vector3 diff in testDiffs)
//                {
//                    float errorRange = _axisSelectRange;

//                    if (diff.X > _axisHalfLDist &&
//                        Math.Abs(diff.Y) < errorRange &&
//                        Math.Abs(diff.Z) < errorRange)
//                        _hiX = true;
//                    if (diff.Y > _axisHalfLDist &&
//                        Math.Abs(diff.X) < errorRange &&
//                        Math.Abs(diff.Z) < errorRange)
//                        _hiY = true;
//                    if (diff.Z > _axisHalfLDist &&
//                        Math.Abs(diff.X) < errorRange &&
//                        Math.Abs(diff.Y) < errorRange)
//                        _hiZ = true;

//                    if (snapFound = _hiX || _hiY || _hiZ)
//                        break;
//                }

//                if (!snapFound)
//                {
//                    foreach (Vector3 diff in testDiffs)
//                    {
//                        if (_mode == TransformType.Translate)
//                        {
//                            if (diff.X < _axisHalfLDist &&
//                                diff.Y < _axisHalfLDist &&
//                                diff.Z < _axisHalfLDist)
//                            {
//                                //Point lies inside the double drag areas
//                                if (diff.X > _axisSelectRange)
//                                    _hiX = true;
//                                if (diff.Y > _axisSelectRange)
//                                    _hiY = true;
//                                if (diff.Z > _axisSelectRange)
//                                    _hiZ = true;

//                                _hiCirc = !_hiX && !_hiY && !_hiZ;

//                                snapFound = true;
//                            }
//                        }
//                        else if (_mode == TransformType.Scale)
//                        {
//                            //Determine if the point is in the double or triple drag triangles
//                            float halfDist = _scaleHalf2LDist;
//                            float centerDist = _scaleHalf1LDist;
//                            if (diff.IsInTriangle(new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, halfDist, 0)))
//                                if (diff.IsInTriangle(new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, centerDist, 0)))
//                                    _hiX = _hiY = _hiZ = true;
//                                else
//                                    _hiX = _hiY = true;
//                            else if (diff.IsInTriangle(new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, 0, halfDist)))
//                                if (diff.IsInTriangle(new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, 0, centerDist)))
//                                    _hiX = _hiY = _hiZ = true;
//                                else
//                                    _hiX = _hiZ = true;
//                            else if (diff.IsInTriangle(new Vector3(), new Vector3(0, halfDist, 0), new Vector3(0, 0, halfDist)))
//                                if (diff.IsInTriangle(new Vector3(), new Vector3(0, centerDist, 0), new Vector3(0, 0, centerDist)))
//                                    _hiX = _hiY = _hiZ = true;
//                                else
//                                    _hiY = _hiZ = true;

//                            snapFound = _hiX || _hiY || _hiZ;
//                        }

//                        if (snapFound)
//                            break;
//                    }
//                }
//            }

//            return false;
//        }

//        public void Render()
//        {
//            switch (_mode)
//            {
//                case TransformType.Translate:

//                    break;
//                case TransformType.Rotate:

//                    break;
//                case TransformType.Scale:

//                    break;
//            }
//        }
//    }
//}
