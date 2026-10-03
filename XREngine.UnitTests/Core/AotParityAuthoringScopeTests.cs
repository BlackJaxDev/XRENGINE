using NUnit.Framework;
using Shouldly;
using XREngine.Data.Runtime.AotParity;

namespace XREngine.UnitTests.Core;

[TestFixture]
[NonParallelizable]
public sealed class AotParityAuthoringScopeTests
{
    [Test]
    public void AuthoringScope_RestoresLogicalAndSynchronousPlayerScopes()
    {
        EAotParityMode previousMode = AotParityDiagnostics.Mode;
        try
        {
            AotParityDiagnostics.ConfigureMode(EAotParityMode.Error);
            using (AotParityDiagnostics.EnterPlayerPath(EAotParityPlayerPathKind.PlayMode))
            using (AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode))
            {
                AotParityDiagnostics.IsPlayerPath.ShouldBeTrue();
                using (AotParityDiagnostics.EnterSynchronousAuthoringPath())
                {
                    AotParityDiagnostics.IsPlayerPath.ShouldBeFalse();
                    using (AotParityDiagnostics.EnterSynchronousPlayerPath(EAotParityPlayerPathKind.PlayMode))
                        AotParityDiagnostics.IsPlayerPath.ShouldBeTrue();
                    AotParityDiagnostics.IsPlayerPath.ShouldBeFalse();
                }
                AotParityDiagnostics.IsPlayerPath.ShouldBeTrue();
            }
            AotParityDiagnostics.IsPlayerPath.ShouldBeFalse();
        }
        finally
        {
            AotParityDiagnostics.ConfigureMode(previousMode);
        }
    }

    [Test]
    public void AuthoringScope_RestoresPlayerPathAfterException()
    {
        EAotParityMode previousMode = AotParityDiagnostics.Mode;
        try
        {
            AotParityDiagnostics.ConfigureMode(EAotParityMode.Error);
            using (AotParityDiagnostics.EnterPlayerPath(EAotParityPlayerPathKind.PlayMode))
            {
                Should.Throw<InvalidOperationException>(() =>
                {
                    using var authoring = AotParityDiagnostics.EnterSynchronousAuthoringPath();
                    AotParityDiagnostics.IsPlayerPath.ShouldBeFalse();
                    throw new InvalidOperationException();
                });
                AotParityDiagnostics.IsPlayerPath.ShouldBeTrue();
            }
        }
        finally
        {
            AotParityDiagnostics.ConfigureMode(previousMode);
        }
    }
}
