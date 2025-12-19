using MagicPhysX;
using System.Numerics;
using XREngine;
using XREngine.Components.Physics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using static XREngine.Scene.Transforms.RigidBodyTransform;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static class Physics
    {
        private const ushort CollisionGroup = 1;
        private static readonly PhysicsGroupsMask CollisionMask = new(0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF);

        //Creates a floor and a bunch of balls that fall onto it.
        public static void AddPhysics(SceneNode rootNode, int ballCount)
        {
            AddPhysicsFloor(rootNode);
            AddPhysicsSpheres(rootNode, ballCount);
        }

        public static void AddPhysicsSpheres(SceneNode rootNode, int count, float radius = 0.5f)
        {
            if (count <= 0)
                return;

            Random random = new();
            PhysxMaterial physMat = new(0.2f, 0.2f, 1.0f);
            physMat.RestitutionCombineMode = ECombineMode.Max;
            for (int i = 0; i < count; i++)
                AddBall(rootNode, physMat, radius, random);
        }

        public static void AddPhysicsFloor(SceneNode rootNode)
        {
            var floor = new SceneNode(rootNode) { Name = "Floor" };
            var floorTfm = floor.SetTransform<RigidBodyTransform>();
            var floorComp = floor.AddComponent<StaticRigidBodyComponent>()!;

            PhysxMaterial floorPhysMat = new(0.5f, 0.5f, 0.7f);
            //floorPhysMat.RestitutionCombineMode = ECombineMode.Max;

            Vector3 floorHalfExtents = new(5000.0f, 0.5f, 5000.0f);
            floorComp.Material = floorPhysMat;
            floorComp.Geometry = new IPhysicsGeometry.Box(floorHalfExtents);
            floorComp.InitialPosition = new Vector3(0.0f, -floorHalfExtents.Y, 0.0f);
            floorComp.InitialRotation = Quaternion.Identity;
            floorComp.CollisionGroup = CollisionGroup;
            floorComp.GroupsMask = CollisionMask;

            void OnFloorActivated(SceneNode node)
            {
                //if (node.World?.PhysicsScene is PhysxScene scene)
                //{
                //    scene.BounceThresholdVelocity = 0.01f;
                //    Debug.Physics($"[UnitTestingWorld.Physics] Set BounceThresholdVelocity to {scene.BounceThresholdVelocity}");
                //}

                EnsureStaticRigidBodyReady(node, floorComp);
                //ForceFloorOverlapProbe(node, floorComp);
                node.Activated -= OnFloorActivated;
            }

            floor.Activated += OnFloorActivated;
            //floorBody.AddedToScene += x =>
            //{
            //    var shapes = floorBody.GetShapes();
            //    var shape = shapes[0];
            //    //shape.QueryFilterData = new MagicPhysX.PxFilterData() { word0 = 0, word1 = 0, word2 = 0, word3 = 1 };
            //};

            //var floorShader = ShaderHelper.LoadEngineShader("Misc\\TestFloor.frag");
            //ShaderVar[] floorUniforms =
            //[
            //    new ShaderVector4(new ColorF4(0.9f, 0.9f, 0.9f, 1.0f), "MatColor"),
            //    new ShaderFloat(10.0f, "BlurStrength"),
            //    new ShaderInt(20, "SampleCount"),
            //    new ShaderVector3(Globals.Up, "PlaneNormal"),
            //];
            //XRTexture2D grabTex = XRTexture2D.CreateGrabPassTextureResized(0.2f);
            //XRMaterial floorMat = new(floorUniforms, [grabTex], floorShader);
            XRMaterial floorMat = XRMaterial.CreateLitColorMaterial(ColorF4.Gray);
            floorMat.RenderOptions.CullMode = ECullMode.None;
            //floorMat.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            floorMat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
            //floorMat.EnableTransparency();

            var floorModel = floor.AddComponent<ModelComponent>()!;
            floorModel.Model = new Model([new SubMesh(XRMesh.Create(VertexQuad.PosY(10000.0f)), floorMat)
        {
            //CullingBounds = new AABB(
            //    new Vector3(-5000f, -0.001f, -5000f),
            //    new Vector3(5000f, 0.001f, 5000f)
            //)
        }]);
        }

        //Spawns a ball with a random position, velocity and angular velocity.
        public static void AddBall(SceneNode rootNode, PhysxMaterial ballPhysMat, float ballRadius, Random random)
        {
            const float spawnRange = 40.0f;
            float minSpawnHeight = MathF.Max(ballRadius * 2.0f, 5.0f);
            Vector3 spawnPosition = new(
                (random.NextSingle() - 0.5f) * spawnRange,
                minSpawnHeight + random.NextSingle() * spawnRange,
                (random.NextSingle() - 0.5f) * spawnRange);

            Vector3 angularVelocity = new(
                random.NextSingle() * 100.0f,
                random.NextSingle() * 100.0f,
                random.NextSingle() * 100.0f);

            Vector3 linearVelocity = new(
                random.NextSingle() * 10.0f,
                random.NextSingle() * 10.0f,
                random.NextSingle() * 10.0f);

            var ball = new SceneNode(rootNode) { Name = "Ball" };
            var ballTfm = ball.SetTransform<RigidBodyTransform>();
            ballTfm.InterpolationMode = EInterpolationMode.Interpolate;
            var ballComp = ball.AddComponent<DynamicRigidBodyComponent>()!;
            ballComp.Material = ballPhysMat;
            ballComp.Geometry = new IPhysicsGeometry.Sphere(ballRadius);
            ballTfm.SetPositionAndRotation(spawnPosition, Quaternion.Identity);
            /*
            ballComp.InitialPosition = spawnPosition;
            ballComp.InitialRotation = Quaternion.Identity;
            */
            ballComp.Density = 1.0f;
            ballComp.LinearDamping = 0.0f;
            ballComp.AngularDamping = 0.0f;
            ballComp.BodyFlags |= PhysicsRigidBodyFlags.EnableCcd | PhysicsRigidBodyFlags.EnableSpeculativeCcd | PhysicsRigidBodyFlags.EnableCcdFriction;
            ballComp.CollisionGroup = CollisionGroup;
            ballComp.GroupsMask = CollisionMask;

            void OnBallActivated(SceneNode node)
            {
                ApplyInitialVelocities();
                //ForceBallOverlapProbe(node, ballComp);
                node.Activated -= OnBallActivated;
            }

            void ApplyInitialVelocities()
            {
                if (ballComp.RigidBody is PhysxDynamicRigidBody physxBody)
                {
                    physxBody.SetAngularVelocity(angularVelocity);
                    physxBody.SetLinearVelocity(linearVelocity);
                }
                else
                {
                    Debug.Physics("[UnitTestingWorld.Physics] Skipped initial velocities for {0}: RigidBody not ready", ball.Name);
                }
            }

            ball.Activated += OnBallActivated;
            var ballModel = ball.AddComponent<ModelComponent>()!;

            ColorF4 color = new(
                random.NextSingle(),
                random.NextSingle(),
                random.NextSingle());

            var ballMat = XRMaterial.CreateLitColorMaterial(color);
            ballMat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
            ballMat.Parameter<ShaderFloat>("Roughness")!.Value = random.NextSingle();
            ballMat.Parameter<ShaderFloat>("Metallic")!.Value = random.NextSingle();
            ballModel.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, ballRadius, 32), ballMat)]);
        }

        private static unsafe void ForceBallOverlapProbe(SceneNode ballNode, DynamicRigidBodyComponent ballComp)
        {
            string nodeLabel = NodeLabel(ballNode);
            if (ballComp.Geometry is not IPhysicsGeometry geometry)
            {
                Debug.Physics("[UnitTestingWorld.Physics] Overlap probe skipped for {0}: no geometry", nodeLabel);
                return;
            }

            if (ballNode.World?.PhysicsScene is not PhysxScene physxScene)
            {
                Debug.Physics("[UnitTestingWorld.Physics] Overlap probe skipped for {0}: PhysX scene unavailable", nodeLabel);
                return;
            }

            if (ballComp.RigidBody is not PhysxRigidActor physxActor)
            {
                Debug.Physics("[UnitTestingWorld.Physics] Overlap probe skipped for {0}: PhysX actor not ready", nodeLabel);
                return;
            }

            var pose = physxActor.Transform;
            var hits = physxScene.OverlapMultiple(
                geometry,
                pose,
                PxQueryFlags.Static | PxQueryFlags.Dynamic,
                null,
                null,
                32);

            if (hits.Length == 0)
            {
                Debug.Physics(
                    "[UnitTestingWorld.Physics] Overlap probe for {0} at {1} reported 0 hits",
                    nodeLabel,
                    FormatVector(pose.position));
                ForceBallRaycastProbe(ballNode, ballComp, pose);
                return;
            }

            Debug.Physics(
                "[UnitTestingWorld.Physics] Overlap probe for {0} at {1} reported {2} hits",
                nodeLabel,
                FormatVector(pose.position),
                hits.Length);

            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                var hitActor = PhysxRigidActor.Get(hit.actor);
                var hitShape = physxScene.GetShape(hit.shape);
                var actorName = hitActor?.GetOwningComponent()?.Name ?? hitActor?.GetType().Name ?? "<null-actor>";
                var shapeName = hitShape?.Name ?? "<null-shape>";
                var hitGroup = hitActor?.CollisionGroup ?? 0;
                var hitMask = hitActor is PhysxActor physActor ? FormatGroupsMask(physActor.GroupsMask) : "----";
                Debug.Physics(
                    "    [UnitTestingWorld.Physics] hit#{0}: actor={1} group={2} mask={3} shape={4} faceIndex={5}",
                    i,
                    actorName,
                    hitGroup,
                    hitMask,
                    shapeName,
                    hit.faceIndex);
            }

            ForceBallRaycastProbe(ballNode, ballComp, pose);
        }

        private static string FormatVector(Vector3 v)
            => $"({v.X:0.000}, {v.Y:0.000}, {v.Z:0.000})";

        private static string FormatGroupsMask(PxGroupsMask mask)
            => $"{mask.bits0:X4}:{mask.bits1:X4}:{mask.bits2:X4}:{mask.bits3:X4}";

        private static void EnsureStaticRigidBodyReady(SceneNode node, StaticRigidBodyComponent floorComp)
        {
            string nodeLabel = NodeLabel(node);
            if (floorComp.RigidBody is PhysxStaticRigidBody physxStatic)
            {
                Debug.Physics(
                    "[UnitTestingWorld.Physics] Floor rigid body ready actorType={0} group={1} mask={2}",
                    physxStatic.GetType().Name,
                    physxStatic.CollisionGroup,
                    FormatGroupsMask(physxStatic.GroupsMask));
            }
            else
            {
                Debug.Physics(
                    "[UnitTestingWorld.Physics] Floor rigid body not ready yet (node={0})",
                    nodeLabel);
            }
        }

        private static unsafe void ForceBallRaycastProbe(SceneNode ballNode, DynamicRigidBodyComponent ballComp, (Vector3 position, Quaternion rotation) pose)
        {
            string nodeLabel = NodeLabel(ballNode);
            if (ballNode.World?.PhysicsScene is not PhysxScene physxScene)
            {
                Debug.Physics("[UnitTestingWorld.Physics] Raycast probe skipped for {0}: PhysX scene unavailable", nodeLabel);
                return;
            }

            Vector3 origin = pose.position;
            if (ballComp.Geometry is IPhysicsGeometry.Sphere sphere)
            {
                origin += Vector3.UnitY * -(sphere.Radius + 0.1f);
            }

            Vector3 direction = Vector3.UnitY * -1.0f;
            const float distance = 2000.0f;
            PxRaycastHit hit;
            bool hasHit = physxScene.RaycastSingle(
                origin,
                direction,
                distance,
                PxHitFlags.Position | PxHitFlags.Normal,
                out hit,
                PxQueryFlags.Static | PxQueryFlags.Dynamic,
                null,
                null,
                null);

            if (!hasHit)
            {
                Debug.Physics(
                    "[UnitTestingWorld.Physics] Raycast probe for {0} from {1} found no hits",
                    nodeLabel,
                    FormatVector(origin));
                return;
            }

            var hitActor = PhysxRigidActor.Get(hit.actor);
            var actorName = hitActor?.GetOwningComponent()?.Name ?? hitActor?.GetType().Name ?? "<null-actor>";
            Debug.Physics(
                "[UnitTestingWorld.Physics] Raycast probe for {0} hit actor={1} distance={2:0.000} normal={3}",
                nodeLabel,
                actorName,
                hit.distance,
                FormatVector(hit.normal));
        }

        private static unsafe void ForceFloorOverlapProbe(SceneNode floorNode, StaticRigidBodyComponent floorComp)
        {
            string nodeLabel = NodeLabel(floorNode);

            if (floorComp.Geometry is not IPhysicsGeometry geometry)
            {
                Debug.Physics("[UnitTestingWorld.Physics] Floor overlap probe skipped: no geometry for {0}", nodeLabel);
                return;
            }

            if (floorNode.World?.PhysicsScene is not PhysxScene physxScene)
            {
                Debug.Physics("[UnitTestingWorld.Physics] Floor overlap probe skipped: PhysX scene unavailable for {0}", nodeLabel);
                return;
            }

            Vector3 position = floorComp.InitialPosition ?? floorNode.Transform.WorldTranslation;
            Quaternion rotation = floorComp.InitialRotation ?? floorNode.Transform.WorldRotation;
            var pose = (position: position, rotation: rotation);

            var hits = physxScene.OverlapMultiple(
                geometry,
                pose,
                PxQueryFlags.Static | PxQueryFlags.Dynamic,
                null,
                null,
                32);

            if (hits.Length == 0)
            {
                Debug.Physics(
                    "[UnitTestingWorld.Physics] Floor overlap probe at {0} reported 0 hits",
                    FormatVector(pose.position));
                return;
            }

            Debug.Physics(
                "[UnitTestingWorld.Physics] Floor overlap probe at {0} reported {1} hits",
                FormatVector(pose.position),
                hits.Length);

            for (int i = 0; i < hits.Length; i++)
            {
                var hit = hits[i];
                var hitActor = PhysxRigidActor.Get(hit.actor);
                var actorName = hitActor?.GetOwningComponent()?.Name ?? hitActor?.GetType().Name ?? "<null-actor>";
                var hitGroup = hitActor?.CollisionGroup ?? 0;
                var hitMask = hitActor is PhysxActor physActor ? FormatGroupsMask(physActor.GroupsMask) : "----";
                Debug.Physics(
                    "    [UnitTestingWorld.Physics] floor-hit#{0}: actor={1} group={2} mask={3} faceIndex={4}",
                    i,
                    actorName,
                    hitGroup,
                    hitMask,
                    hit.faceIndex);
            }
        }

        private static string NodeLabel(SceneNode node)
            => string.IsNullOrWhiteSpace(node.Name) ? "<unnamed-node>" : node.Name!;
    }
}