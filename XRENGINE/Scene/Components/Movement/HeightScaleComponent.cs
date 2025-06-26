using System.Numerics;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering.Models;
using XREngine.Scene.Transforms;

namespace XREngine.Components
{
    /// <summary>
    /// Handles all scaling of the avatar's height to synchronize it with the real-world height of the user.
    /// </summary>
    public class HeightScaleComponent : XRComponent
    {
        public HeightScaleComponent()
        {
            Engine.VRState.ModelHeightChanged += UpdateHeightScale;
            Engine.VRState.DesiredAvatarHeightChanged += UpdateHeightScale;
            Engine.VRState.RealWorldHeightChanged += UpdateHeightScale;
        }
        protected override void OnDestroying()
        {
            base.OnDestroying();
            Engine.VRState.ModelHeightChanged -= UpdateHeightScale;
            Engine.VRState.DesiredAvatarHeightChanged -= UpdateHeightScale;
            Engine.VRState.RealWorldHeightChanged -= UpdateHeightScale;
        }

        public float RadiusRatio { get; set; } = 0.2f;
        public float CrouchedHeightRatio { get; set; } = 0.5f;
        public float ProneHeightRatio { get; set; } = 0.2f;

        private Vector3 _eyeOffsetFromHead = Vector3.Zero;
        public Vector3 EyeOffsetFromHead
        {
            get => _eyeOffsetFromHead;
            set => SetField(ref _eyeOffsetFromHead, value);
        }

        private TransformBase? _footTransform;
        public TransformBase? FootTransform
        {
            get => _footTransform;
            set => SetField(ref _footTransform, value);
        }

        public Vector3 ScaledToRealWorldEyeOffsetFromHead => EyeOffsetFromHead * Engine.VRState.ModelToRealWorldHeightRatio;

        public HumanoidComponent? HumanoidComponent { get; set; }
        public CharacterMovement3DComponent? CharacterMovementComponent { get; set; }

        public HumanoidComponent? GetHumanoid()
            => HumanoidComponent ?? GetSiblingComponent<HumanoidComponent>();
        public CharacterMovement3DComponent? GetCharacterMovement()
            => CharacterMovementComponent ?? GetSiblingComponent<CharacterMovement3DComponent>();

        private void UpdateHeightScale(float value)
        {
            HumanoidComponent? humanoid = GetHumanoid();
            if (humanoid is null)
                return;

            CharacterMovement3DComponent? movement = GetCharacterMovement();
            if (movement is null)
                return;

            float height = Engine.VRState.ModelHeight * Engine.VRState.ModelToRealWorldHeightRatio;

            TransformBase rootTfm = humanoid.Transform;
            TransformBase? footTfm = FootTransform ?? rootTfm.Parent;

            float radius = height * RadiusRatio;
            float radius2 = radius * 2.0f;
            float capsuleHeight = height - radius2;

            movement.StandingHeight = capsuleHeight;
            movement.CrouchedHeight = capsuleHeight * CrouchedHeightRatio;
            movement.ProneHeight = capsuleHeight * ProneHeightRatio;
            movement.Radius = radius;

            //Move the foot halfway down the capsule
            if (footTfm is not null)
            {
                Vector3 translation = new(0.0f, -movement.HalfHeight, 0.0f);
                if (footTfm is Transform tfm)
                    tfm.Translation = translation;
                else
                    footTfm.DeriveLocalMatrix(Matrix4x4.CreateTranslation(translation));
            }

            //Scale the root transform to match the real-world height
            Vector3 scale = new(Engine.VRState.ModelToRealWorldHeightRatio);
            if (rootTfm is Transform transform)
                transform.Scale = scale;
            else
                rootTfm.DeriveLocalMatrix(Matrix4x4.CreateScale(scale));
        }

        /// <summary>
        /// Calculates the 
        /// </summary>
        public void MeasureAvatarHeight()
        {
            var h = GetHumanoid();
            if (h is null)
                return;

            var headNode = h.Head.Node;
            if (headNode is null)
                return;

            var rootTfm = h.SceneNode.Transform;
            var headTfm = headNode.Transform;

            float eyeY = headTfm.BindMatrix.Translation.Y + EyeOffsetFromHead.Y;
            float footY = rootTfm.BindMatrix.Translation.Y;
            float height = eyeY - footY;

            Debug.Out($"Calculated model height as {height}");
            Engine.VRState.ModelHeight = height;
        }

        /// <summary>
        /// Calculates the average position of all vertices rigged to bones that contain the word "eye" in their name and returns the difference from the head bone.
        /// </summary>
        /// <returns></returns>
        public void CalculateEyeOffsetFromHead(ModelComponent? eyesModel, string? eyeLBoneName, string? eyeRBoneName, bool forceXToZero = true)
        {
            EyeOffsetFromHead = Vector3.Zero;

            var h = GetHumanoid();
            if (h is null)
                return;

            if (eyesModel is null)
                return;

            var headNode = h.Head.Node;
            if (headNode is null)
                return;

            //Collect all vertices rigged to bones that contain the word "eye"
            EventList<SubMesh>? meshes = eyesModel.Model?.Meshes; //TODO: use submeshes, or renderable meshes?
            if (meshes is null || meshes.Count == 0)
                return;

            //Find lods with matching eye bones
            int lodCount = 0;
            Vector3 avgEyePos = Vector3.Zero;
            foreach (SubMesh mesh in meshes)
            {
                var lod = mesh.LODs.FirstOrDefault();
                if (lod is null)
                    continue;

                var bones = lod.Mesh?.UtilizedBones;
                if (bones is null || bones.Length == 0)
                    continue;

                if (bones.Any(b => IsEyeBone(b.tfm, eyeLBoneName, eyeRBoneName)))
                {
                    SumEyeVertexPositions(lod, out Vector3 eyePosWorldAvg, eyeLBoneName, eyeRBoneName);
                    lodCount++;
                    avgEyePos += eyePosWorldAvg;
                }
            }
            avgEyePos /= lodCount;

            if (forceXToZero)
                avgEyePos.X = 0;

            Vector3 rootToHead = headNode.Transform.WorldMatrix.Translation - Transform.WorldMatrix.Translation;
            avgEyePos -= rootToHead;

            //if (forceXToZero)
            //    avgEyePos.X = 0;

            EyeOffsetFromHead = avgEyePos;
        }

        private static bool SumEyeBonePositions((TransformBase tfm, Matrix4x4 invBindWorldMtx)[] bones, out Vector3 eyePosWorldAvg, string? eyeLBoneName, string? eyeRBoneName)
        {
            int counted = 0;
            eyePosWorldAvg = Vector3.Zero;

            foreach (var (tfm, invBindWorldMtx) in bones)
            {
                if (!IsEyeBone(tfm, eyeLBoneName, eyeRBoneName))
                    continue;

                eyePosWorldAvg += tfm.WorldTranslation;
                counted++;
            }

            bool any = counted > 0;
            if (any)
                eyePosWorldAvg /= counted;
            return any;
        }

        // Helper method for atomic addition on float
        private static float AtomicAdd(ref float target, float value)
        {
            float initialValue, computedValue;
            do
            {
                initialValue = target;
                computedValue = initialValue + value;
            }
            while (Interlocked.CompareExchange(ref target, computedValue, initialValue) != initialValue);
            return computedValue;
        }

        private static void SumEyeVertexPositions(SubMeshLOD? lod, out Vector3 eyePosWorldAvg, string? eyeLBoneName, string? eyeRBoneName)
        {
            if (lod is null)
            {
                eyePosWorldAvg = Vector3.Zero;
                return;
            }
            var mesh = lod.Mesh;
            if (mesh is null)
            {
                eyePosWorldAvg = Vector3.Zero;
                return;
            }

            eyePosWorldAvg = Vector3.Zero;

            float sumX = 0f, sumY = 0f, sumZ = 0f;
            int counted = 0;

            Parallel.ForEach(mesh.Vertices, vertex =>
            {
                var weights = vertex.Weights;
                if (weights is null)
                    return;

                bool hasEyeBone = weights.Any(w => IsEyeBone(w.Key, eyeLBoneName, eyeRBoneName));
                if (!hasEyeBone)
                    return;

                Vector3 pos = vertex.GetWorldPosition();
                AtomicAdd(ref sumX, pos.X);
                AtomicAdd(ref sumY, pos.Y);
                AtomicAdd(ref sumZ, pos.Z);
                Interlocked.Increment(ref counted);
            });

            eyePosWorldAvg = new Vector3(sumX, sumY, sumZ);
            bool any = counted > 0;
            if (any)
                eyePosWorldAvg /= counted;
        }

        private static bool IsEyeBone(TransformBase tfm, string? eyeLBoneName, string? eyeRBoneName)
        {
            string? name = tfm.Name;
            if (name is null)
                return false;

            if (eyeLBoneName is not null && name.Contains(eyeLBoneName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (eyeRBoneName is not null && name.Contains(eyeRBoneName, StringComparison.OrdinalIgnoreCase))
                return true;

            return name.Contains("eye", StringComparison.OrdinalIgnoreCase);
        }
    }
}