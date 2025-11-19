using System.Numerics;
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
            for (int i = 0; i < count; i++)
                AddBall(rootNode, physMat, radius, random);
        }

        public static void AddPhysicsFloor(SceneNode rootNode)
        {
            var floor = new SceneNode(rootNode) { Name = "Floor" };
            var floorTfm = floor.SetTransform<RigidBodyTransform>();
            var floorComp = floor.AddComponent<StaticRigidBodyComponent>()!;

            PhysxMaterial floorPhysMat = new(0.5f, 0.5f, 0.7f);

            Vector3 floorHalfExtents = new(5000.0f, 0.5f, 5000.0f);
            floorComp.Material = floorPhysMat;
            floorComp.Geometry = new IPhysicsGeometry.Box(floorHalfExtents);
            floorComp.InitialPosition = new Vector3(0.0f, -floorHalfExtents.Y, 0.0f);
            floorComp.InitialRotation = Quaternion.Identity;
            floorComp.CollisionGroup = CollisionGroup;
            floorComp.GroupsMask = CollisionMask;
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
            ballComp.InitialPosition = spawnPosition;
            ballComp.InitialRotation = Quaternion.Identity;
            ballComp.Density = 1.0f;
            ballComp.LinearDamping = 0.2f;
            ballComp.AngularDamping = 0.2f;
            ballComp.BodyFlags |= PhysicsRigidBodyFlags.EnableCcd | PhysicsRigidBodyFlags.EnableSpeculativeCcd | PhysicsRigidBodyFlags.EnableCcdFriction;
            ballComp.CollisionGroup = CollisionGroup;
            ballComp.GroupsMask = CollisionMask;

            void ApplyInitialVelocities(SceneNode node)
            {
                if (ballComp.RigidBody is PhysxDynamicRigidBody physxBody)
                {
                    physxBody.SetAngularVelocity(angularVelocity);
                    physxBody.SetLinearVelocity(linearVelocity);
                }
                node.Activated -= ApplyInitialVelocities;
            }
            ball.Activated += ApplyInitialVelocities;
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
    }
}