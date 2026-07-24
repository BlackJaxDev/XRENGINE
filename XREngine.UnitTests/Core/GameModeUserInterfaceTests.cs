using NUnit.Framework;
using Shouldly;
using XREngine.Components;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class GameModeUserInterfaceTests
{
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
}
