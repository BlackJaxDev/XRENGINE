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

            var floorBody = PhysxStaticRigidBody.CreatePlane(Globals.Up, 0.0f, floorPhysMat);
            //new PhysxStaticRigidBody(floorMat, new PhysxGeometry.Box(new Vector3(100.0f, 2.0f, 100.0f)));
            floorBody.SetTransform(new Vector3(0.0f, 0.0f, 0.0f), Quaternion.CreateFromAxisAngle(Globals.Forward, XRMath.DegToRad(90.0f)), true);
            //floorBody.CollisionGroup = 1;
            //floorBody.GroupsMask = new MagicPhysX.PxGroupsMask() { bits0 = 0, bits1 = 0, bits2 = 0, bits3 = 1 };
            floorComp.RigidBody = floorBody;
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
            floorMat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
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
            var ballBody = new PhysxDynamicRigidBody(ballPhysMat, new IPhysicsGeometry.Sphere(ballRadius), 1.0f)
            {
                Transform = (new Vector3(
                    random.NextSingle() * 100.0f,
                    random.NextSingle() * 100.0f,
                    random.NextSingle() * 100.0f), Quaternion.Identity),
                AngularDamping = 0.2f,
                LinearDamping = 0.2f,
            };

            ballBody.SetAngularVelocity(new Vector3(
                random.NextSingle() * 100.0f,
                random.NextSingle() * 100.0f,
                random.NextSingle() * 100.0f));

            ballBody.SetLinearVelocity(new Vector3(
                random.NextSingle() * 10.0f,
                random.NextSingle() * 10.0f,
                random.NextSingle() * 10.0f));

            var ball = new SceneNode(rootNode) { Name = "Ball" };
            var ballTfm = ball.SetTransform<RigidBodyTransform>();
            ballTfm.InterpolationMode = EInterpolationMode.Interpolate;
            var ballComp = ball.AddComponent<DynamicRigidBodyComponent>()!;
            ballComp.RigidBody = ballBody;
            var ballModel = ball.AddComponent<ModelComponent>()!;

            ColorF4 color = new(
                random.NextSingle(),
                random.NextSingle(),
                random.NextSingle());

            var ballMat = XRMaterial.CreateLitColorMaterial(color);
            ballMat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferredLit;
            ballMat.Parameter<ShaderFloat>("Roughness")!.Value = random.NextSingle();
            ballMat.Parameter<ShaderFloat>("Metallic")!.Value = random.NextSingle();
            ballModel.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, ballRadius, 32), ballMat)]);
        }
    }
}