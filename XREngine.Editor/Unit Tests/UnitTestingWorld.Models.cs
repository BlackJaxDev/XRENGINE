using Assimp;
using Extensions;
using System.Numerics;
using XREngine;
using XREngine.Animation;
using XREngine.Animation.IK;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Components.Movement;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Components;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Quaternion = System.Numerics.Quaternion;

namespace XREngine.Editor;

public static partial class UnitTestingWorld
{
    public static class Models
    {
        public static void ImportModels(string desktopDir, SceneNode rootNode, SceneNode characterParentNode)
        {
            if (Toggles.CreateUnitBox)
            {
                SceneNode boxNode = new(rootNode) { Name = "UnitBox" };
                ModelComponent boxComp = boxNode.AddComponent<ModelComponent>()!;
                var mesh = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true, XRMesh.Shapes.ECubemapTextureUVs.None);
                var material = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Red);
                material.RenderOptions.CullMode = ECullMode.None;
                material.RenderPass = (int)EDefaultRenderPass.OpaqueForward;
                boxComp.Model = new Model([new SubMesh(mesh, material)
                {
                    CullingBounds = 
                        new AABB(new Vector3(-0.5f),
                        new Vector3(0.5f)),
                }]);
            }

            if (Toggles.ImportAnimatedModel)
            {
                SceneNode? ImportAnimated()
                {
                    string fbxPathDesktop = Path.Combine(desktopDir, Toggles.AnimatedModelDesktopPath);
                    if (!File.Exists(fbxPathDesktop))
                    {
                        Debug.LogWarning($"Animated model file not found at {fbxPathDesktop}");
                        return null;
                    }
                    using var importer = new ModelImporter(fbxPathDesktop, null, null);
                    importer.MakeMaterialAction = CreateHardcodedMaterial;
                    importer.MakeTextureAction = CreateHardcodedTexture;
                    var node = importer.Import(Toggles.AnimatedModelImportFlags, true, true, Toggles.AnimatedModelScale, Toggles.AnimatedModelZUp, true);
                    if (characterParentNode != null && node != null)
                        characterParentNode.Transform.AddChild(node.Transform, false, true);
                    return node;
                }
                Task.Run(ImportAnimated).ContinueWith(nodeTask => OnFinishedImportingAvatar(nodeTask.Result, characterParentNode));
                //ModelImporter.ImportAsync(fbxPathDesktop, animFlags, null, null, characterParentNode, ModelScale, ModelZUp).ContinueWith(OnFinishedAvatarAsync);
            }
            if (Toggles.ImportStaticModel)
            {
                Debug.Out($"[StaticModel] ImportStaticModel is enabled, creating parent node...");
                var importedModelsNode = new SceneNode(rootNode) { Name = "Static Model Root", Layer = DefaultLayers.StaticIndex };
                Debug.Out($"[StaticModel] Created 'Static Model Root' node, parent is '{rootNode.Name}'");
                string path = Path.Combine(Engine.Assets.EngineAssetsPath, "Models", "Sponza", "sponza.obj");
                Debug.Out($"[StaticModel] Model path: {path}");

                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[StaticModel] Static model file not found at {path}");
                }
                else
                {
                    Debug.Out($"[StaticModel] File exists, scheduling import job...");

                    ModelImporter.DelMakeMaterialAction makeMaterialAction = Toggles.StaticModelMaterialMode switch
                    {
                        StaticModelMaterialMode.Deferred => ModelImporter.MakeMaterialDeferred,
                        StaticModelMaterialMode.ForwardPlusTextured => ModelImporter.MakeMaterialForwardPlusTextured,
                        StaticModelMaterialMode.ForwardPlusUberShader => ModelImporter.MakeMaterialForwardPlusUberShader,
                        _ => ModelImporter.MakeMaterialDeferred,
                    };

                    // Use the job system for streaming mesh import.
                    // The scene node tree is created first, then meshes are added one-by-one as they complete.
                    var job = ModelImporter.ScheduleImportJob(
                        path,
                        Toggles.StaticModelImportFlags,
                        onFinished: result =>
                        {
                            Debug.Out($"[StaticModel] onFinished callback invoked");
                            // Scene node tree is already built and parented by this point.
                            // Meshes have been streaming in as they complete.
                            if (result.RootNode != null)
                            {
                                Debug.Out($"[StaticModel] RootNode is not null: '{result.RootNode.Name}'");
                                Debug.Out($"[StaticModel] RootNode parent: '{result.RootNode.Parent?.Name ?? "NULL"}'");
                                Debug.Out($"[StaticModel] RootNode transform parent: '{result.RootNode.Transform.Parent?.SceneNode?.Name ?? "NULL"}'");
                                Debug.Out($"[StaticModel] RootNode world position: {result.RootNode.Transform.WorldTranslation}");
                                result.RootNode.GetTransformAs<Transform>()?.ApplyScale(new Vector3(0.01f));
                                Debug.Out($"[StaticModel] Applied scale, new world position: {result.RootNode.Transform.WorldTranslation}");
                                Debug.Out($"[StaticModel] Import completed: {result.Meshes.Count} meshes, {result.Materials.Count} materials");
                            }
                            else
                            {
                                Debug.LogWarning($"[StaticModel] onFinished: result.RootNode is NULL!");
                            }
                        },
                        onError: ex =>
                        {
                            Debug.LogException(ex, $"[StaticModel] Failed to import static model: {path}");
                        },
                        onCanceled: () => Debug.LogWarning($"[StaticModel] Import was canceled: {path}"),
                        onProgress: progress => Debug.Out($"[StaticModel] Progress: {progress:P0}"),
                        cancellationToken: default,
                        parent: importedModelsNode,
                        scaleConversion: 1.0f,
                        zUp: false,
                        materialFactory: null,
                        makeMaterialAction: makeMaterialAction,
                        layer: DefaultLayers.StaticIndex);
                    Debug.Out($"[StaticModel] Import job scheduled, job ID: {job?.GetHashCode()}");
                }
            }
        }

        public static void AddSkybox(SceneNode rootNode, XRTexture2D skyEquirect)
        {
            var skybox = new SceneNode(rootNode) { Name = "TestSkyboxNode" };
            if (!skybox.TryAddComponent<SkyboxComponent>(out var skyboxComp))
                return;

            skyboxComp!.Name = "TestSkybox";
            skyboxComp.Projection = ESkyboxProjection.Equirectangular;
            skyboxComp.Texture = skyEquirect;
            skyboxComp.Intensity = 1.0f;
        }

        private static void OnFinishedAvatarAsync(Task<(SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes)> task)
        {
            if (task.IsCanceled || task.IsFaulted)
                return;

            (SceneNode? rootNode, IReadOnlyCollection<XRMaterial> materials, IReadOnlyCollection<XRMesh> meshes) = task.Result;
            OnFinishedImportingAvatar(rootNode);
        }
        public static void OnFinishedImportingAvatar(SceneNode? rootNode)
            => OnFinishedImportingAvatar(rootNode, characterParentNode: null);

        public static void OnFinishedImportingAvatar(SceneNode? rootNode, SceneNode? characterParentNode)
        {
            if (rootNode is null)
                return;

            //Debug.Out(rootNode.PrintTree());

            var humanComp = rootNode.AddComponent<HumanoidComponent>()!;
            humanComp.SolveIK = false;
            humanComp.LeftArmIKEnabled = false;
            humanComp.RightArmIKEnabled = false;
            humanComp.LeftLegIKEnabled = false;
            humanComp.RightLegIKEnabled = false;
            humanComp.HipToHeadIKEnabled = false;

            var animator = rootNode.AddComponent<AnimStateMachineComponent>()!;

            // VRHeightScaleComponent depends on VRState. For desktop locomotion we just scale once to fit the capsule.
            HeightScaleBaseComponent? heightScale = null;
            if (Toggles.VRPawn)
                heightScale = rootNode.AddComponent<VRHeightScaleComponent>()!;
            else
            {
                var desktopHeightScale = rootNode.AddComponent<HeightScaleComponent>()!;
                heightScale = desktopHeightScale;
                TryScaleAvatarToFitDesktopCapsule(rootNode, humanComp, desktopHeightScale, characterParentNode);
            }
            
            if (Toggles.FaceTracking)
            {
                const string vrcftPrefix = "/avatar/parameters/";
                var ftOscReceiver = rootNode.AddComponent<FaceTrackingReceiverComponent>()!;
                ftOscReceiver.ParameterPrefix = vrcftPrefix;
                ftOscReceiver.GenerateARKitStateMachine();

                var ftOscSender = rootNode.AddComponent<OscSenderComponent>()!;
                ftOscSender.ParameterPrefix = vrcftPrefix;

                /// <summary>
                /// Called when a variable changes in a state machine.
                /// </summary>
                /// <param name="variable"></param>
                void StateMachineVariableChanged(AnimVar variable)
                {
                    string address = variable.ParameterName;
                    switch (variable)
                    {
                        case AnimFloat f:
                            ftOscSender.Send(address, f.Value);
                            break;
                        case AnimInt i:
                            ftOscSender.Send(address, i.Value);
                            break;
                        case AnimBool b:
                            ftOscSender.Send(address, b.Value);
                            break;
                    }
                }

                animator!.StateMachine.VariableChanged += StateMachineVariableChanged;
            }

            VRIKSolverComponent? vrIKSolver = null;
            if (Toggles.AddCharacterIK)
            {
                if (!Toggles.VRPawn)
                {
                    var humanik = rootNode.AddComponent<HumanoidIKSolverComponent>()!;

                    humanik.SetIKPositionWeight(ELimbEndEffector.LeftHand, 1.0f);
                    humanik.SetIKRotationWeight(ELimbEndEffector.LeftHand, 1.0f);

                    humanik.SetIKPositionWeight(ELimbEndEffector.RightHand, 1.0f);
                    humanik.SetIKRotationWeight(ELimbEndEffector.RightHand, 1.0f);

                    humanik.SetIKPositionWeight(ELimbEndEffector.LeftFoot, 1.0f);
                    humanik.SetIKRotationWeight(ELimbEndEffector.LeftFoot, 1.0f);

                    humanik.SetIKPositionWeight(ELimbEndEffector.RightFoot, 1.0f);
                    humanik.SetIKRotationWeight(ELimbEndEffector.RightFoot, 1.0f);

                    humanik.SetSpineWeight(0.0f);

                    SceneNode ikTargetNode = rootNode.NewChild("IKTargetNode");
                    var handTfm = humanComp.Left.Foot.Node!.GetTransformAs<Transform>(true)!;
                    ikTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
                    {
                        Order = ETransformOrder.TRS,
                        Rotation = handTfm.WorldRotation,
                        Scale = new Vector3(1.0f),
                        Translation = handTfm.WorldTranslation
                    });
                    humanik.GetGoalIK(ELimbEndEffector.LeftFoot)!.TargetIKTransform = ikTargetNode.Transform;
                    UserInterface.EnableTransformToolForNode(ikTargetNode);
                    Selection.SceneNode = ikTargetNode;
                }
                else
                {
                    var vrik = rootNode.AddComponent<VRIKSolverComponent>()!;
                    vrik.IsActive = false;
                    vrIKSolver = vrik;
                    //rootNode.AddComponent<VRIKRootControllerComponent>();
                    //vrik.GuessHandOrientations();
                }
            }

            //TODO: only remove the head in VR
            //if (Toggles.VRPawn || !Toggles.ThirdPersonPawn)
            //{
            //    var headTfm = humanComp.Head?.Node?.GetTransformAs<Transform>();
            //    if (headTfm is not null)
            //        headTfm.Scale = Vector3.Zero;
            //}

            if (Toggles.Locomotion)
                Pawns.InitializeLocomotion(rootNode, humanComp, heightScale, vrIKSolver);
            
            if (Toggles.TestAnimation)
            {
                var knee = humanComp!.Right.Knee?.Node?.Transform;
                var leg = humanComp!.Right.Leg?.Node?.Transform;

                leg?.RegisterAnimationTick<Transform>(t => t.Rotation = Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(180 - 90.0f * (MathF.Cos(Engine.ElapsedTime) * 0.5f + 0.5f))));
                knee?.RegisterAnimationTick<Transform>(t => t.Rotation = Quaternion.CreateFromAxisAngle(Globals.Right, XRMath.DegToRad(90.0f * (MathF.Cos(Engine.ElapsedTime) * 0.5f + 0.5f))));

                //var rootTfm = rootNode.FirstChild.GetTransformAs<Transform>(true)!;
                ////rotate the root node in a circle, but still facing forward
                //rootTfm.RegisterAnimationTick<Transform>(t =>
                //{
                //    t.Translation = new Vector3(0, MathF.Sin(Engine.ElapsedTime), 0);
                //});

                //For testing blendshape morphing
                //Technically we should only register animation tick on scene node if any lods have blendshapes,
                //but at this point the model's meshes are still loading. So we'll just be lazy and check in the animation tick since it's just for testing.
                rootNode.IterateComponents<ModelComponent>(comp =>
                {
                    comp.SceneNode.RegisterAnimationTick<SceneNode>(t =>
                    {
                        var renderers = comp.Meshes.SelectMany(x => x.LODs).Select(x => x.Renderer).Where(x => x?.Mesh?.HasBlendshapes ?? false);
                        foreach (var renderer in renderers)
                        {
                            //for (int r = 0; r < xrMesh!.BlendshapeCount; r++)
                            int r = 0;
                            renderer?.SetBlendshapeWeightNormalized((uint)r, MathF.Sin(Engine.ElapsedTime) * 0.5f + 0.5f);
                        }
                    });
                }, true);
            }

            if (Toggles.PhysicsChain)
                CreateHardcodedPhysicsChains(humanComp);
            
            if (Toggles.TransformTool)
            {
                //Show the transform tool for testing
                var root = humanComp!.SceneNode;
                if (root is null)
                    return;

                UserInterface.EnableTransformToolForNode(root);
            }

            if (Toggles.VMC)
            {
                var vmc = rootNode.AddComponent<VMCCaptureComponent>()!;
                vmc.Humanoid = humanComp;
            }

            if (Toggles.FaceMotion3D)
            {
                var face = rootNode.AddComponent<FaceMotion3DCaptureComponent>()!;
                face.Humanoid = humanComp;
                //deactivate glasses node
                var glasses = humanComp.SceneNode?.FindDescendant(x => x.Name?.Contains("glasses", StringComparison.InvariantCultureIgnoreCase) ?? false);
                glasses?.IsActiveSelf = false;
            }

            if (Toggles.AnimationClipVMD)
            {
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var clip = Engine.Assets.Load<AnimationClip>(Path.Combine(desktopDir, "test.vmd"));
                if (clip is not null)
                {
                    clip.Looped = true;

                    var anim = rootNode.AddComponent<AnimationClipComponent>()!;
                    anim.StartOnActivate = true;
                    anim.Animation = clip;
                }
            }

            OVRLipSyncComponent? lipSync = null;
            if (Toggles.AttachMicToAnimatedModel)
            {
                var headNode = humanComp!.Head?.Node?.Transform?.SceneNode;
                if (headNode is not null)
                    Audio.AttachMicTo(headNode, out _, out _, out lipSync);
            }
            else if (Toggles.LipSync)
                lipSync = Engine.State.MainPlayer.ControlledPawn?.SceneNode?.GetComponent<OVRLipSyncComponent>();

            if (lipSync is not null)
            {
                var face = rootNode.FindDescendantByName("Face", StringComparison.InvariantCultureIgnoreCase);
                if (face is not null)
                {
                    if (face.TryGetComponent<ModelComponent>(out var comp))
                        lipSync.ModelComponent = comp;
                    else
                    {
                        face.ComponentAdded += SetModel;
                        void SetModel((SceneNode node, XRComponent comp) x)
                        {
                            if (x.comp is ModelComponent model)
                                lipSync.ModelComponent = model;
                            face.ComponentAdded -= SetModel;
                        }
                    }
                }
            }
        }

        private static void TryScaleAvatarToFitDesktopCapsule(
            SceneNode avatarRoot,
            HumanoidComponent humanoid,
            HeightScaleComponent heightScale,
            SceneNode? characterParentNode)
        {
            // Determine the character capsule dimensions from the desktop character movement component.
            var characterNode = characterParentNode?.Parent;
            var movement = characterNode?.GetComponent<CharacterMovement3DComponent>();
            if (movement is null)
                return;

            // Measure model height in bind space using the humanoid head vs root, similar to VRHeightScaleComponent.MeasureAvatarHeight.
            var headNode = humanoid.Head.Node;
            if (headNode is null)
                return;

            float headY = headNode.Transform.BindMatrix.Translation.Y;
            float rootY = humanoid.SceneNode.Transform.BindMatrix.Translation.Y;
            float modelHeight = headY - rootY;
            if (!float.IsFinite(modelHeight) || modelHeight <= 0.001f)
                return;

            float capsuleOuterHeight = movement.CurrentHeight + 2.0f * (movement.Radius + movement.ContactOffset);
            if (!float.IsFinite(capsuleOuterHeight) || capsuleOuterHeight <= 0.001f)
                return;

            // Use the HeightScaleComponent that was just added to apply measured height + a single uniform scale.
            // Keep the desktop character capsule as the source of truth: only scale down (never up) to fit.
            float rawRatio = capsuleOuterHeight / modelHeight;
            if (!float.IsFinite(rawRatio) || rawRatio <= 0.0f)
                return;

            float ratio = MathF.Min(1.0f, rawRatio);

            // Ensure the component has what it needs without letting it resize the character capsule.
            heightScale.HumanoidComponent = humanoid;
            heightScale.CharacterMovementComponent = null;

            heightScale.ApplyMeasuredHeight(modelHeight);
            heightScale.RealWorldHeightRatio = ratio;
        }

        #region Hardcoded stuff, varies per model
        private static void CreateHardcodedPhysicsChains(HumanoidComponent humanComp)
        {
            //Add physics chain to the breast bone
            var chest = humanComp!.Chest?.Node?.Transform;
            //Find breast bone
            if (chest is not null)
            {
                var earR = chest.FindDescendant(x =>
                    (x.Name?.Contains("KittenEarR", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (earR?.SceneNode is not null)
                {
                    var phys = earR.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var earL = chest.FindDescendant(x =>
                    (x.Name?.Contains("KittenEarL", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (earL?.SceneNode is not null)
                {
                    var phys = earL.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var breastL = chest.FindDescendant(x =>
                    (x.Name?.Contains("BreastUpper2_LRoot", StringComparison.InvariantCultureIgnoreCase) ?? false) ||
                    (x.Name?.Contains("Boob.L", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (breastL?.SceneNode is not null)
                {
                    var phys = breastL.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                var breastR = chest.FindDescendant(x =>
                    (x.Name?.Contains("BreastUpper2_RRoot", StringComparison.InvariantCultureIgnoreCase) ?? false) ||
                    (x.Name?.Contains("Boob.R", StringComparison.InvariantCultureIgnoreCase) ?? false));
                if (breastR?.SceneNode is not null)
                {
                    var phys = breastR.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.0f;
                    phys.Stiffness = 0.05f;
                    //phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Elasticity = 0.2f;
                    phys.Multithread = false;
                }

                //var breastCenter = chest.FindChild(x => (x.Name?.Contains("Boob_Root", StringComparison.InvariantCultureIgnoreCase) ?? false));
                //if (breastCenter?.SceneNode is not null)
                //{
                //    var phys = breastCenter.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.1f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.05f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.2f;
                //    phys.Multithread = false;
                //}

                var tail = humanComp.Hips?.Node?.Transform.FindDescendant(x =>
                    x.Name?.Contains("Hair_1_2", StringComparison.InvariantCultureIgnoreCase) ?? false);
                if (tail?.SceneNode is not null)
                {
                    var phys = tail.SceneNode.AddComponent<PhysicsChainComponent>()!;
                    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Default;
                    phys.UpdateRate = 60;
                    phys.Damping = 0.1f;
                    phys.Inert = 0.5f;
                    phys.Stiffness = 0.05f;
                    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                    phys.Gravity = new Vector3(0.0f, -0.1f, 0.0f);
                    phys.Elasticity = 0.01f;
                    phys.Multithread = false;
                }

                //var longHair = humanComp.Head?.Node?.Transform.FindDescendant(x =>
                //    x.Name?.Contains("Long Hair", StringComparison.InvariantCultureIgnoreCase) ?? false);
                //if (longHair?.SceneNode is not null)
                //{
                //    //longHair.SceneNode.IsActiveSelf = false;
                //    var phys = longHair.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.Normal;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.1f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.05f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.2f;
                //}

                //var zafHair = humanComp.Head?.Node?.Transform.FindChild(x => x.Name?.Contains("Zaf Hair", StringComparison.InvariantCultureIgnoreCase) ?? false);
                //if (zafHair?.SceneNode is not null)
                //{
                //    var phys = zafHair.SceneNode.AddComponent<PhysicsChainComponent>()!;
                //    phys.UpdateMode = PhysicsChainComponent.EUpdateMode.FixedUpdate;
                //    phys.UpdateRate = 60;
                //    phys.Damping = 0.01f;
                //    phys.Inert = 0.0f;
                //    phys.Stiffness = 0.01f;
                //    phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
                //    phys.Elasticity = 0.1f;
                //}
            }
        }

        //Hardcoded materials for testing until UI
        public static void CreateHardcodedMaterial(XRMaterial mat, XRTexture[] textureList, List<TextureSlot> textures, string name)
        {
            //Debug.Out($"Making material for {name}: {string.Join(", ", textureList.Select(x => x?.Name ?? "<missing name>"))}");

            // Clear current shader list
            mat.Shaders.Clear();

            XRShader color = ShaderHelper.LitColorFragDeferred()!;
            XRShader albedo = ShaderHelper.LitTextureFragDeferred()!;
            XRShader albedoNormal = ShaderHelper.LitTextureNormalFragDeferred()!;
            XRShader albedoNormalMetallic = ShaderHelper.LitTextureNormalMetallicFragDeferred()!;
            XRShader albedoMetallic = ShaderHelper.LitTextureMetallicFragDeferred()!;
            XRShader albedoNormalRoughnessMetallic = ShaderHelper.LitTextureNormalRoughnessMetallicDeferred()!;
            XRShader albedoRoughness = ShaderHelper.LitTextureRoughnessFragDeferred()!;
            XRShader albedoMatcap = ShaderHelper.LitTextureMatcapDeferred()!;
            XRShader albedoEmissive = ShaderHelper.LitTextureEmissiveDeferred();

            switch (name)
            {
                //case "Boots": // "BaseColor, Roughness, BaseColor, , Roughness"
                //              //textureList[0].Load3rdParty();
                //    mat.Shaders.Add(albedoRoughness);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //    textureList[1],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Metal": // "T_MainTex_D, T_MainTex_D"
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    mat.SetVector3("BaseColor", new Vector3(0.6f));
                //    mat.SetFloat("Roughness", 0.5f);
                //    mat.SetFloat("Metallic", 1.0f);
                //    mat.SetFloat("Specular", 1.0f);
                //    break;

                //case "BackHair": // "HairEarTail Texture, HairEarTail Texture"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Goth_Bunny_Straps": // "Arm matcaps, Arm matcaps"
                //    mat.Shaders.Add(albedoMatcap);
                //    mat.Textures =
                //    [
                //        textureList.TryGet(0),
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "black_1": // "T_MainTex_D, T_MainTex_D, "
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Ears": // "ears emiss, ears emiss"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #130": // no textures provided
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #132": // "BLACK, NORMAL, METALIC, Regular_Roughness"
                //    mat.Shaders.Add(albedoNormalRoughnessMetallic);
                //    mat.Textures =
                //    [
                //        textureList[0],
                //    textureList[1],
                //    textureList[2],
                //    textureList[3],
                //];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "1": // "No Saturation 06 (1), No Saturation 06 (1)
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "2": // "61, 61"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Goth_Bunny_Thigh_Highs": // "generator5, generator5"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "LEFT_EYE": // "Eye_5, left eye by nanna, Eye_5"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Back_hair": // "Hair1, hairglow-mono_emission, Hair1"
                //    mat.Shaders.Add(albedoEmissive);
                //    mat.Textures = [textureList[0], textureList[1]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "New_002": // "lis_dlcu_029_02_base_PP1_K, lis_dlcu_029_02_base_PP1_K"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Mat_Glow": // "T_MainTex_D, T_MainTex_D"
                //    mat.Shaders.Add(color);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "FABRIC 1_FRONT_1830": // No textures given
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #131": // "7"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "tail": // "T_MainTex_D, EM backhair, T_MainTex_D"
                //    mat.Shaders.Add(albedoEmissive);
                //    mat.Textures = [textureList[0], textureList[1]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "hoodie": // "low poly_defaultMat.001_BaseColor.1001, low poly_defaultMat.001_Roughness.1001, "
                //    mat.Shaders.Add(albedoRoughness);
                //    mat.Textures = [textureList[0], textureList[1]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Face": // "googleface, googleface"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material #98": // "Shorts_bake_Merge, Shorts_Normal_OpenGL"
                //    mat.Shaders.Add(albedoNormal);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Material.002": // Empty setup
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "metal.002": // Empty setup
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                //case "Body": // "Body, Body"
                //    mat.Shaders.Add(albedo);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "RIGHT_EYE": // "Eye_5, right eye by nanna, Eye_5"
                //    mat.Shaders.Add(color);
                //    mat.Textures = [textureList[0]];
                //    MakeDefaultParameters(mat);
                //    break;

                //case "TEETH___SOCK": // "T_MainTex_D, T_MainTex_D"
                //    mat.Shaders.Add(color);
                //    MakeDefaultParameters(mat);
                //    break;

                default:
                    // Default material setup - use textured deferred if we have valid albedo textures
                    if (textureList.Length > 0 && textureList[0] is not null)
                    {
                        mat.Shaders.Add(albedo);
                        mat.Textures = [textureList[0]];
                    }
                    else
                    {
                        mat.Shaders.Add(color);
                    }
                    MakeDefaultParameters(mat);
                    break;
            }
            mat.Name = name;
            // Set a default render pass (opaque deferred lighting in this example)
            mat.RenderPass = (int)EDefaultRenderPass.OpaqueDeferred;
        }
        public static XRTexture2D CreateHardcodedTexture(string path)
        {
            Dictionary<string, string> pathRemap = new()
            {
                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\BLACK\\BLACK.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\BLACK\\BLACK.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\NORMAL.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\NORMAL.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\METALIC.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\METALIC.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Adidas_Superstar\\Regular_Roughness.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Adidas_Superstar\\Regular_Roughness.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\1\\T_MainTex_D.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\BaseColor.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\BaseColor.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\Roughness.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Shoes\\Leather High Boots Extended\\Leather High Boots Extended\\2k_base\\Roughness.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\generator5.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\generator5.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\ears emiss.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\ears emiss.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Underwear\\Female Bombshell Bra\\Female Bombshell Bra\\Texture_Valentines Bra Black Lace trans.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Underwear\\Female Bombshell Bra\\Female Bombshell Bra\\Texture_Valentines Bra Black Lace trans.png"
                },

                {
                    "D:\\Documents\\Avatar2\\Assets\\Zafira Model By Luuy\\Tex\\lis_dlcu_029_02_base_PP1_K.png",
                    ""
                },

                {
                    "D:\\Documents\\Avatar2\\Assets\\Zafira Model By Luuy\\Tex\\Hair1.png",
                    ""
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Tops\\Shawty hoodie - Zinpia\\7.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Tops\\Shawty hoodie - Zinpia\\7.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\BedHead v2 by Nessy\\Textures\\61.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Hair\\HairPack WetCat\\Textures\\61.png"
                },

                {
                    "D:\\Documents\\Avatar2\\Assets\\Zafira Model By Luuy\\Tex\\hairglow-mono_emission.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\R\\Avatar-Lovelylove-Asset-bundle-2.file_ad87b39f-eb28-49ba-bae7-ab7bc0f312fc.1.vrca\\Assets\\Texture2D\\hairglow-mono_emission 1.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\Misc\\Main\\X\\Val\\Jax.fbm\\Eye_5.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\Eye_5.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\Misc\\Main\\X\\Val\\Texture2D\\Body.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Texture2D\\Body.png" },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\left eye by nanna.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\left eye by nanna.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\EM backhair.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\EM backhair.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\Bedhead by Nessy\\Textures\\No Saturation 01.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Textures\\Cici Hair\\No Saturation 01.png"
                },

                {
                    "D:\\Documents\\Avatar2\\Assets\\Main\\Avatars\\MAD LOVE\\Materials\\Textures\\HairEarTail Texture.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Texture2D\\HairEarTail Texture.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\Misc\\Main\\X\\Val\\Jax.fbm\\googleface.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\googleface.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_BaseColor.1001.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_BaseColor.1001.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_Roughness.1001.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Tops\\panda split dye hoodie by clally#6969\\split dye hoodie for panda.fbm\\low poly_defaultMat.001_Roughness.1001.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\Arm matcaps.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\Arm matcaps.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\BedHead v2 by Nessy\\Textures\\51.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Hair\\Jen by Nessy\\Textures\\51.png"
                },

                {
                    "D:\\Documents\\Avatar2\\Assets\\Main\\Rips\\worker_lXgVip0x3vX06jqcB6_zGr9UoSbJw1C1PGuPZ-tmp1\\Assets\\Texture2D\\T_MainTex_D.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\T_MainTex_D.png"
                },

                {
                    "C:\\Users\\black\\OneDrive\\Desktop\\misc\\..\\..\\..\\VRChat Assets\\Hair\\Bedhead by Nessy\\Textures\\No Saturation 06 (1).png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\VRChat Assets\\Textures\\Cici Hair\\No Saturation 06.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Shorts_bake_Merge.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Jax VRM Export\\Jax VRM2.Textures\\Shorts_bake_Merge.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Shorts_Normal_OpenGL.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\! Saint By Hate\\Textures\\Shorts_Normal_OpenGL.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\right eye by nanna.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\Misc\\Main\\X\\Val\\Jax.fbm\\right eye by nanna.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Shorts_bake_Merge_metal.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\!Textures\\Split By Yumiko\\Shorts_bake_Merge_metal.png"
                },

                {
                    "L:\\CustomAvatars2\\Assets\\VRChat Assets\\Pants\\Shorts by Zeit\\Shorts_By_Zeit_rar\\Metal_Normal_OpenGL.png",
                    "C:\\Users\\black\\OneDrive\\Documents\\VRC-Avatars\\Assets\\JaxVRCToolkit_Autogenerated\\AshOld\\Textures\\Metal_Normal_OpenGL.png"
                }
            };

            if (!File.Exists(path) && pathRemap.TryGetValue(path, out string? newPath) && !string.IsNullOrEmpty(newPath))
                path = newPath;

            var tex = Engine.Assets.Load<XRTexture2D>(path);
            if (tex is null)
            {
                Debug.Out($"Failed to load texture: {path}");
                tex = new XRTexture2D()
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    MagFilter = ETexMagFilter.Linear,
                    MinFilter = ETexMinFilter.Linear,
                    UWrap = ETexWrapMode.Repeat,
                    VWrap = ETexWrapMode.Repeat,
                    AlphaAsTransparency = true,
                    AutoGenerateMipmaps = true,
                    Resizable = true,
                };
            }
            else
            {
                //Debug.Out($"Loaded texture: {path}");
                tex.MagFilter = ETexMagFilter.Linear;
                tex.MinFilter = ETexMinFilter.Linear;
                tex.UWrap = ETexWrapMode.Repeat;
                tex.VWrap = ETexWrapMode.Repeat;
                tex.AlphaAsTransparency = true;
                tex.AutoGenerateMipmaps = true;
                tex.Resizable = false;
                tex.SizedInternalFormat = ESizedInternalFormat.Rgba8;
            }
            return tex;
        }
        #endregion

        private static void MakeDefaultParameters(XRMaterial mat)
            => mat.Parameters =
            [
                new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
                new ShaderFloat(1.0f, "Opacity"),
                new ShaderFloat(1.0f, "Roughness"),
                new ShaderFloat(0.0f, "Metallic"),
                new ShaderFloat(0.0f, "Specular"),
                new ShaderFloat(0.0f, "Emission"),
            ];
    }
}