using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Components;
using XREngine.Input;
using XREngine.Networking;
using XREngine.Players;
using XREngine.Scene;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class GameModeUserInterfaceTests
{
    [Test]
    public void BeginPlay_PreservesAuthoredPawnPossessedInSameWorld()
    {
        TestWorld world = new();
        SceneNode node = new(world, "Authored Pawn");
        TestPawn pawn = node.AddComponent<TestPawn>()!;
        TestController controller = new() { ControlledPawnComponent = pawn };
        IRuntimeGameModeHostServices? previousHost = RuntimeGameModeHostServices.Current;
        IRuntimePlayerControllerServices? previousPlayers = RuntimePlayerControllerServices.Current;
        TestGameMode mode = new() { WorldInstance = world };

        try
        {
            RuntimeGameModeHostServices.Current = new TestHost();
            RuntimePlayerControllerServices.Current = new TestPlayers(controller);

            mode.OnBeginPlay();

            mode.SpawnCount.ShouldBe(0);
            controller.ControlledPawnComponent.ShouldBeSameAs(pawn);
        }
        finally
        {
            if (mode.IsActive)
                mode.OnEndPlay();
            RuntimePlayerControllerServices.Current = previousPlayers;
            RuntimeGameModeHostServices.Current = previousHost;
            node.Destroy(now: true);
            mode.Destroy(now: true);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BeginPlay_SpawnsWhenPawnIsMissingOrFromAnotherWorld(bool hasForeignPawn)
    {
        TestWorld world = new();
        SceneNode? foreignNode = hasForeignPawn ? new SceneNode(new TestWorld(), "Foreign Pawn") : null;
        TestController controller = new()
        {
            ControlledPawnComponent = foreignNode?.AddComponent<TestPawn>(),
        };
        IRuntimeGameModeHostServices? previousHost = RuntimeGameModeHostServices.Current;
        IRuntimePlayerControllerServices? previousPlayers = RuntimePlayerControllerServices.Current;
        TestGameMode mode = new() { WorldInstance = world };

        try
        {
            RuntimeGameModeHostServices.Current = new TestHost();
            RuntimePlayerControllerServices.Current = new TestPlayers(controller);

            mode.OnBeginPlay();

            mode.SpawnCount.ShouldBe(1);
        }
        finally
        {
            if (mode.IsActive)
                mode.OnEndPlay();
            RuntimePlayerControllerServices.Current = previousPlayers;
            RuntimeGameModeHostServices.Current = previousHost;
            foreignNode?.Destroy(now: true);
            mode.Destroy(now: true);
        }
    }

    [Test]
    public void LocomotionGameMode_DeclaresAnEmptyRuntimeCanvas()
    {
        var gameMode = new LocomotionGameMode();

        gameMode.PlayerUserInterfaceClass.ShouldBe(typeof(UICanvasComponent));
        typeof(IRuntimeGameModeUserInterface)
            .IsAssignableFrom(gameMode.PlayerUserInterfaceClass)
            .ShouldBeTrue();
    }

    [Test]
    public void CustomGameMode_AcceptsOnlyRuntimeUserInterfaceComponents()
    {
        var gameMode = new CustomGameMode
        {
            DefaultPlayerUserInterfaceClass = typeof(UICanvasComponent),
        };

        gameMode.PlayerUserInterfaceClass.ShouldBe(typeof(UICanvasComponent));
        Should.Throw<ArgumentException>(
            () => gameMode.DefaultPlayerUserInterfaceClass = typeof(PawnComponent));
    }

    private sealed class TestGameMode : GameMode
    {
        public int SpawnCount { get; private set; }

        protected override XRComponent? SpawnDefaultPlayerPawn(ELocalPlayerIndex playerIndex)
        {
            SpawnCount++;
            return null;
        }
    }

    public sealed class TestPawn : XRComponent { }

    private sealed class TestController : IPawnController
    {
        public bool IsLocal => true;
        public PlayerInfo? PlayerInfo => null;
        public object? InputDevice => null;
        public object? Viewport { get; set; }
        public object? FocusedInteractable { get; set; }
        public XRComponent? ControlledPawnComponent { get; set; }
        public ELocalPlayerIndex? LocalPlayerIndex => ELocalPlayerIndex.One;
        public void TickPawnInput(float delta, bool isUIInputCaptured) { }
        public void OnPawnCameraChanged() { }
        public void EnqueuePossession(XRComponent pawn) => ControlledPawnComponent = pawn;
        public void ApplyNetworkTransform(PlayerTransformUpdate update) { }
    }

    private sealed class TestPlayers(TestController controller) : IRuntimePlayerControllerServices
    {
        public event Action<IPawnController>? LocalPlayerAdded { add { } remove { } }
        public event Action<IPawnController>? LocalPlayerRemoved { add { } remove { } }
        public IPawnController? GetLocalPlayer(ELocalPlayerIndex index) => controller;
        public IPawnController GetOrCreateLocalPlayer(ELocalPlayerIndex index, Type? controllerTypeOverride = null) => controller;
        public bool RemoveLocalPlayer(ELocalPlayerIndex index) => false;
        public IPawnController MainPlayer => controller;
        public int LocalPlayerCount => 1;
        public IReadOnlyList<IPawnController?> AllLocalPlayers => [controller];
        public IPawnController CreateRemotePlayer(int serverPlayerIndex) => throw new NotSupportedException();
        public IReadOnlyList<IPawnController> RemotePlayers => [];
        public void AddRemotePlayer(IPawnController player) => throw new NotSupportedException();
        public bool RemoveRemotePlayer(IPawnController player) => false;
    }

    private sealed class TestHost : IRuntimeGameModeHostServices
    {
        public bool AutoSpawnPlayer => true;
        public ELocalPlayerIndex DefaultPlayerIndex => ELocalPlayerIndex.One;
        public Type? DefaultPawnType => typeof(TestPawn);
        public string? GetWorldName(object? worldInstance) => "Test World";
        public XRComponent? CreatePawn(object worldInstance, string nodeName, Type pawnType) => throw new NotSupportedException();
        public XRComponent? CreatePlayerUserInterface(object worldInstance, string nodeName, Type userInterfaceType, XRComponent pawn) => null;
        public (Vector3 Position, Quaternion Rotation) GetSpawnPoint(ELocalPlayerIndex playerIndex) => (Vector3.Zero, Quaternion.Identity);
        public void ApplySpawnTransform(XRComponent pawn, Vector3 position, Quaternion rotation) { }
        public void DestroyPlayerUserInterface(XRComponent pawn, XRComponent userInterface) { }
        public void DestroyPawn(XRComponent pawn) { }
    }

    private sealed class TestWorld : IRuntimeWorldContext
    {
        public bool IsPlaySessionActive => true;
        public void RegisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void UnregisterTick(ETickGroup group, int order, WorldTick tick) { }
        public void AddDirtyRuntimeObject(RuntimeWorldObjectBase worldObject) { }
        public void EnqueueRuntimeWorldMatrixChange(RuntimeWorldObjectBase worldObject, Matrix4x4 worldMatrix) { }
    }
}
