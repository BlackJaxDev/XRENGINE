using NUnit.Framework;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.UnitTests.Physics;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks state delivery and callback ownership on both registry routes.</summary>
[TestFixture, NonParallelizable]
public sealed class NativeDockPanelStateRoutingTests
{
    private SceneNode _sourceNode = null!;
    private SceneNode _destinationNode = null!;
    private NativeDockWindowRegistration _source = null!;
    private NativeDockWindowRegistration _destination = null!;
    private readonly object _context = new();
    private string _sourceKey = null!;
    private string _destinationKey = null!;

    [SetUp]
    public void SetUp()
    {
        TestWorldContext world = new();
        _sourceNode = new("State source");
        _destinationNode = new("State destination");
        _sourceNode.SetWorldContext(world);
        _destinationNode.SetWorldContext(world);
        _source = _sourceNode.AddComponent<NativeDockWindowRegistration>()!;
        _destination = _destinationNode.AddComponent<NativeDockWindowRegistration>()!;
        _sourceKey = Guid.NewGuid().ToString("N");
        _destinationKey = Guid.NewGuid().ToString("N");
    }

    [TearDown]
    public void TearDown()
    {
        _sourceNode.Destroy(true);
        _destinationNode.Destroy(true);
    }

    private NativeDockTransferResult Transfer(bool pointed, string panel = NativeDockPanel.Console)
        => pointed
            ? NativeDockWindowRegistry.TransferToWindow(_source, _destination,
                panel, "target-stack")
            : NativeDockWindowRegistry.TransferToAnotherWindow(_source, panel);

    private void ConfigureDestination(Func<NativeDockPanelTransferState, bool> receive,
        string expectedPanel = NativeDockPanel.Console)
        => _destination.Configure(_destinationKey,
            _ => throw new AssertionException("The stateless receiver must not run."), _context,
            receivePanelAtStack: (_, _) =>
                throw new AssertionException("The stateless pointed receiver must not run."),
            receivePanelState: (panel, state) =>
            {
                Assert.That(panel, Is.EqualTo(expectedPanel));
                return receive(state);
            },
            receivePanelStateAtStack: (panel, stack, state) =>
            {
                Assert.That(panel, Is.EqualTo(expectedPanel));
                Assert.That(stack, Is.EqualTo("target-stack"));
                return receive(state);
            });

    [TestCase(false)]
    [TestCase(true)]
    public void CapturedStateReachesOnlyTheStateReceiver(bool pointed)
    {
        NativeDockLayout layout = new() { ConsoleFilter = "captured", ConsoleAutoScroll = false };
        layout.PanelScrollOffsets[1] = 96;
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(layout,
            NativeDockPanel.Console);
        _source.Configure(_sourceKey, _ => true, _context,
            capturePanelState: _ => new NativeDockPanelTransferState(values, () => true));
        int delivered = 0;
        ConfigureDestination(state =>
        {
            delivered++;
            Assert.That(state.Values, Is.EqualTo(values));
            return true;
        });

        Assert.That(Transfer(pointed), Is.EqualTo(NativeDockTransferResult.Committed));
        Assert.That(delivered, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AnimationEditingViewReachesTheMatchingReceiver(bool pointed)
    {
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(new(),
            NativeDockPanel.Animation);
        NativeAnimationPanelViewValues view = new(null, null, 0, 0, null, null, null,
            null, null, null, 0, 0, false, 1, 0, true, -1);
        Assert.That(view.HasValidScalars, Is.True);
        _source.Configure(_sourceKey, _ => true, _context,
            capturePanelState: _ => new NativeDockPanelTransferState(values, () => true, view));
        int delivered = 0;
        ConfigureDestination(state =>
        {
            delivered++;
            Assert.That(state.Values.Panel, Is.EqualTo(NativeDockPanel.Animation));
            Assert.That(state.AnimationView, Is.EqualTo(view));
            return true;
        }, NativeDockPanel.Animation);

        Assert.That(Transfer(pointed, NativeDockPanel.Animation),
            Is.EqualTo(NativeDockTransferResult.Committed));
        Assert.That(delivered, Is.EqualTo(1));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void InvalidSourceCannotAuthorizeClosure(bool pointed, bool invalidatedDuringReceive)
    {
        bool current = invalidatedDuringReceive;
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(new(),
            NativeDockPanel.Console);
        _source.Configure(_sourceKey, _ => true, _context,
            capturePanelState: _ => new NativeDockPanelTransferState(values, () => current));
        int delivered = 0;
        ConfigureDestination(_ => { delivered++; current = false; return true; });

        Assert.That(Transfer(pointed), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(delivered, Is.EqualTo(invalidatedDuringReceive ? 1 : 0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SourceProofCannotReplaceDestinationAndThenDeliverToEitherReceiver(bool pointed)
    {
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(new(),
            NativeDockPanel.Console);
        int delivered = 0;
        ConfigureDestination(_ => { delivered++; return true; });
        _source.Configure(_sourceKey, _ => true, _context,
            capturePanelState: _ => new NativeDockPanelTransferState(values, () =>
            {
                ConfigureDestination(_ => { delivered++; return true; });
                return true;
            }));

        Assert.That(Transfer(pointed), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(delivered, Is.Zero);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void CaptureIdentityRoundTripCannotAuthorizeAReplacementDestination(bool pointed, bool node)
    {
        NativeDockPanelStateValues values = NativeDockPanelStateValues.Capture(new(),
            NativeDockPanel.Console);
        int delivered = 0;
        ConfigureDestination(_ => { delivered++; return true; });
        _source.Configure(_sourceKey, _ => true, _context, capturePanelState: _ =>
        {
            XREngine.Data.Core.XRObjectBase target = node ? _destinationNode : _destination;
            Guid original = target.ID;
            target.AdoptPersistentID(Guid.NewGuid());
            target.AdoptPersistentID(original);
            return new NativeDockPanelTransferState(values, () => true);
        });

        Assert.That(Transfer(pointed), Is.EqualTo(NativeDockTransferResult.Rejected));
        Assert.That(delivered, Is.Zero);
    }
}
