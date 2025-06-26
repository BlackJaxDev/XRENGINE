using Extensions;
using System.IO.Compression;
using System.Numerics;
using XREngine.Data.Core;

namespace XREngine.Scene.Transforms
{
    public abstract partial class TransformBase
    {
        private Matrix4x4 _inverseBindMatrix = Matrix4x4.Identity;
        /// <summary>
        /// Used for model skinning. The inverse model-space bind matrix for this transform, set during model import.
        /// </summary>
        public Matrix4x4 InverseBindMatrix
        {
            get => _inverseBindMatrix;
            set => SetField(ref _inverseBindMatrix, value);
        }

        private Matrix4x4 _bindMatrix = Matrix4x4.Identity;
        public Matrix4x4 BindMatrix
        {
            get => _bindMatrix;
            set => SetField(ref _bindMatrix, value);
        }

        public virtual void SaveBindState()
        {
            BindMatrix = LocalMatrix * (Parent?.BindMatrix ?? Matrix4x4.Identity);
            InverseBindMatrix = Matrix4x4.Invert(BindMatrix, out var inv) ? inv : Matrix4x4.Identity;
        }

        public float DistanceTo(TransformBase other)
            => WorldTranslation.Distance(other.WorldTranslation);
        public float DistanceToParent()
            => WorldTranslation.Distance(Parent?.WorldTranslation ?? Vector3.Zero);

        private float _replicationKeyframeIntervalSec = 5.0f;
        /// <summary>
        /// The interval in seconds between full keyframes sent to the network for this transform.
        /// All other updates are sent as deltas.
        /// </summary>
        public float ReplicationKeyframeIntervalSec
        {
            get => _replicationKeyframeIntervalSec;
            set => SetField(ref _replicationKeyframeIntervalSec, value);
        }

        public float TimeSinceLastKeyframeReplicated => _timeSinceLastKeyframe;

        private bool _forceManualRecalc = false;
        public bool ForceManualRecalc
        {
            get => _forceManualRecalc;
            set => SetField(ref _forceManualRecalc, value);
        }

        public Plane WorldForwardPlane => XRMath.CreatePlaneFromPointAndNormal(WorldTranslation, WorldForward);
        public Plane WorldRightPlane => XRMath.CreatePlaneFromPointAndNormal(WorldTranslation, WorldRight);
        public Plane WorldUpPlane => XRMath.CreatePlaneFromPointAndNormal(WorldTranslation, WorldUp);

        public Plane LocalForwardPlane => XRMath.CreatePlaneFromPointAndNormal(LocalTranslation, LocalForward);
        public Plane LocalRightPlane => XRMath.CreatePlaneFromPointAndNormal(LocalTranslation, LocalRight);
        public Plane LocalUpPlane => XRMath.CreatePlaneFromPointAndNormal(LocalTranslation, LocalUp);

        private float _timeSinceLastKeyframe = 0;

        private Matrix4x4 _lastReplicatedMatrix = Matrix4x4.Identity;
        public byte[] EncodeToBytes()
        {
            _timeSinceLastKeyframe += Engine.Time.Timer.Update.Delta;
            if (_timeSinceLastKeyframe > ReplicationKeyframeIntervalSec)
            {
                _timeSinceLastKeyframe = 0;
                return EncodeToBytes(false);
            }
            else
                return EncodeToBytes(true);
        }

        /// <summary>
        /// Encodes the transform to a byte array for network replication.
        /// By default, this will encode the full matrix if delta is false 
        /// or the difference between the current and last replicated matrix if delta is true.
        /// The receiving end will then call DeriveLocalMatrix to apply the received matrix.
        /// You should override this method and DecodeFromBytes to compress the data as much as possible manually.
        /// </summary>
        /// <param name="delta"></param>
        /// <returns></returns>
        public virtual byte[] EncodeToBytes(bool delta)
        {
            using var memoryStream = new MemoryStream();
            using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal))
            {
                var deltaBytes = BitConverter.GetBytes(delta);
                gzipStream.Write(deltaBytes, 0, deltaBytes.Length);
                if (delta)
                {
                    // Encode only the difference between the current and last replicated transform
                    var deltaMatrix = _localMatrix - _lastReplicatedMatrix;
                    var matrixBytes = MatrixToBytes(deltaMatrix);
                    gzipStream.Write(matrixBytes, 0, matrixBytes.Length);
                }
                else
                {
                    var matrixBytes = MatrixToBytes(_localMatrix);
                    gzipStream.Write(matrixBytes, 0, matrixBytes.Length);
                    _lastReplicatedMatrix = _localMatrix;
                }
            }
            return memoryStream.ToArray();
        }

        /// <summary>
        /// Decodes the transform from a byte array received from network replication.
        /// By default, this will decode the full matrix if delta is false
        /// or the difference between the received and current matrix if delta is true.
        /// This method will then call DeriveLocalMatrix to apply the received matrix.
        /// You should override this method and EncodeToBytes to compress the data as much as possible manually.
        /// </summary>
        /// <param name="arr"></param>
        public virtual void DecodeFromBytes(byte[] arr)
        {
            using var memoryStream = new MemoryStream(arr);
            using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
            var deltaBytes = new byte[sizeof(bool)];
            gzipStream.ReadExactly(deltaBytes);
            bool delta = BitConverter.ToBoolean(deltaBytes, 0);

            var matrixBytes = new byte[16 * sizeof(float)];
            gzipStream.ReadExactly(matrixBytes);
            var matrix = BytesToMatrix(matrixBytes);

            if (delta)
                DeriveLocalMatrix(_localMatrix + matrix);
            else
                DeriveLocalMatrix(matrix);
        }

        private static byte[] MatrixToBytes(Matrix4x4 matrix)
        {
            var bytes = new byte[16 * sizeof(float)];
            Buffer.BlockCopy(new[]
            {
                matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                matrix.M21, matrix.M22, matrix.M23, matrix.M24,
                matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                matrix.M41, matrix.M42, matrix.M43, matrix.M44
            }, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static Matrix4x4 BytesToMatrix(byte[] bytes)
        {
            var values = new float[16];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return new Matrix4x4(
                values[0], values[1], values[2], values[3],
                values[4], values[5], values[6], values[7],
                values[8], values[9], values[10], values[11],
                values[12], values[13], values[14], values[15]
            );
        }

        /// <summary>
        /// Inversely transforms a world position to local space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="worldPosition"></param>
        /// <returns></returns>
        public Vector3 InverseTransformPoint(Vector3 worldPosition)
            => InverseTransformPoint(worldPosition, Engine.IsRenderThread);

        /// <summary>
        /// Transforms a local position to world space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="localPosition"></param>
        /// <returns></returns>
        public Vector3 TransformPoint(Vector3 localPosition)
            => TransformPoint(localPosition, Engine.IsRenderThread);

        /// <summary>
        /// Inversely transforms a world direction to local space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="worldDirection"></param>
        /// <returns></returns>
        public Vector3 InverseTransformDirection(Vector3 worldDirection)
            => InverseTransformDirection(worldDirection, Engine.IsRenderThread);

        /// <summary>
        /// Transforms a local direction to world space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="localDirection"></param>
        /// <returns></returns>
        public Vector3 TransformDirection(Vector3 localDirection)
            => TransformDirection(localDirection, Engine.IsRenderThread);

        /// <summary>
        /// Inversely transforms a world position to local space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="worldPosition"></param>
        /// <param name="render"></param>
        /// <returns></returns>
        public Vector3 InverseTransformPoint(Vector3 worldPosition, bool render)
            => Vector3.Transform(worldPosition, render ? InverseRenderMatrix : InverseWorldMatrix);

        /// <summary>
        /// Transforms a local position to world space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="localPosition"></param>
        /// <param name="render"></param>
        /// <returns></returns>
        public Vector3 TransformPoint(Vector3 localPosition, bool render)
            => Vector3.Transform(localPosition, render ? RenderMatrix : WorldMatrix);

        /// <summary>
        /// Inversely transforms a world direction to local space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="worldDirection"></param>
        /// <param name="render"></param>
        /// <returns></returns>
        public Vector3 InverseTransformDirection(Vector3 worldDirection, bool render)
            => Vector3.TransformNormal(worldDirection, render ? InverseRenderMatrix : InverseWorldMatrix);

        /// <summary>
        /// Transforms a local direction to world space using the appropriate matrix based on the render thread state.
        /// </summary>
        /// <param name="localDirection"></param>
        /// <param name="render"></param>
        /// <returns></returns>
        public Vector3 TransformDirection(Vector3 localDirection, bool render)
            => Vector3.TransformNormal(localDirection, render ? RenderMatrix : WorldMatrix);

        public Quaternion TransformRotation(Quaternion localRotation)
            => Quaternion.Normalize(Quaternion.Concatenate(GetWorldRotation(), localRotation));

        public Quaternion InverseTransformRotation(Quaternion worldRotation)
            => Quaternion.Normalize(Quaternion.Concatenate(GetInverseWorldRotation(), worldRotation));

        public TransformBase? FirstChild()
        {
            lock (_children)
                return _children.FirstOrDefault();
        }
        public TransformBase? LastChild()
        {
            lock (_children)
                return _children.LastOrDefault();
        }

        /// <summary>
        /// Returns the Y-aligned forward and right directions for this transform.
        /// The pitch of the transform is ignored.
        /// </summary>
        /// <param name="forward"></param>
        /// <param name="right"></param>
        public void GetDirectionsXZ(out Vector3 forward, out Vector3 right)
        {
            forward = WorldForward;
            float dot = forward.Dot(Globals.Up);
            if (Math.Abs(dot) >= 0.5f)
            {
                //if dot is 1, looking straight up. need to use camera down for forward
                //if dot is -1, looking straight down. need to use camera up for forward
                forward = dot > 0.0f
                    ? -WorldUp
                    : WorldUp;
            }
            forward.Y = 0.0f;
            forward = forward.Normalized();
            right = WorldRight;
        }

        /// <summary>
        /// Returns the Y-aligned forward and right directions for the provided matrix.
        /// The pitch of the matrix is ignored.
        /// </summary>
        /// <param name="matrix"></param>
        /// <param name="forward"></param>
        /// <param name="right"></param>
        public static void GetDirectionsXZ(Matrix4x4 matrix, out Vector3 forward, out Vector3 right)
        {
            right = Vector3.TransformNormal(Globals.Right, matrix).Normalized();
            forward = Vector3.TransformNormal(Globals.Forward, matrix).Normalized();

            float dot = forward.Dot(Globals.Up);
            if (Math.Abs(dot) >= 0.5f)
            {
                Vector3 up = Vector3.TransformNormal(Globals.Up, matrix);
                //if dot is 1, looking straight up. need to use camera down for forward
                //if dot is -1, looking straight down. need to use camera up for forward
                forward = dot > 0.0f
                    ? -up
                    : up;
            }
            forward.Y = 0.0f;
            forward = forward.Normalized();
        }

        public virtual void ResetPose(bool networkSmoothed = false)
            => DeriveLocalMatrix(ParentInverseBindMatrix * BindMatrix, networkSmoothed);
    }
}