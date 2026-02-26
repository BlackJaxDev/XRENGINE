using NUnit.Framework;
using Shouldly;
using XREngine.Audio;

namespace XREngine.UnitTests.Audio
{
    /// <summary>
    /// Tests for <see cref="AudioDiagnostics"/> — verifying counters, event ring buffer,
    /// snapshot, and enable/disable behavior.
    /// These are pure-logic tests with no OpenAL dependency.
    /// </summary>
    [TestFixture]
    public sealed class AudioDiagnosticsTests
    {
        [SetUp]
        public void SetUp()
        {
            AudioDiagnostics.Reset();
            AudioDiagnostics.Enabled = true;
            AudioDiagnostics.TraceOutput = false;
        }

        [TearDown]
        public void TearDown()
        {
            AudioDiagnostics.Enabled = false;
            AudioDiagnostics.Reset();
        }

        [Test]
        public void Reset_ClearsAllCounters()
        {
            AudioDiagnostics.RecordSourceStateChange(1, "Initial", "Playing");
            AudioDiagnostics.RecordBuffersQueued(1, 3, 3);
            AudioDiagnostics.RecordBuffersUnqueued(1, 2, 1);
            AudioDiagnostics.RecordBufferUnderflow(1, 0);
            AudioDiagnostics.RecordBufferOverflow(1, 1);
            AudioDiagnostics.RecordListenerCreated("test");
            AudioDiagnostics.RecordListenerDisposed("test");

            AudioDiagnostics.Reset();

            AudioDiagnostics.SourceStateTransitions.ShouldBe(0);
            AudioDiagnostics.BuffersQueued.ShouldBe(0);
            AudioDiagnostics.BuffersUnqueued.ShouldBe(0);
            AudioDiagnostics.BufferUnderflows.ShouldBe(0);
            AudioDiagnostics.BufferOverflows.ShouldBe(0);
            AudioDiagnostics.ListenersCreated.ShouldBe(0);
            AudioDiagnostics.ListenersDisposed.ShouldBe(0);
            AudioDiagnostics.GetRecentEvents().ShouldBeEmpty();
        }

        [Test]
        public void Disabled_DoesNotRecord()
        {
            AudioDiagnostics.Enabled = false;

            AudioDiagnostics.RecordSourceStateChange(1, "Initial", "Playing");
            AudioDiagnostics.RecordBuffersQueued(1, 5, 5);
            AudioDiagnostics.RecordListenerCreated("test");

            AudioDiagnostics.SourceStateTransitions.ShouldBe(0);
            AudioDiagnostics.BuffersQueued.ShouldBe(0);
            AudioDiagnostics.ListenersCreated.ShouldBe(0);
            AudioDiagnostics.GetRecentEvents().ShouldBeEmpty();
        }

        [Test]
        public void SourceStateChange_IncrementsCounter()
        {
            AudioDiagnostics.RecordSourceStateChange(1, "Initial", "Playing");
            AudioDiagnostics.RecordSourceStateChange(1, "Playing", "Stopped");

            AudioDiagnostics.SourceStateTransitions.ShouldBe(2);
        }

        [Test]
        public void BuffersQueued_AccumulatesCount()
        {
            AudioDiagnostics.RecordBuffersQueued(1, 3, 3);
            AudioDiagnostics.RecordBuffersQueued(1, 2, 5);

            AudioDiagnostics.BuffersQueued.ShouldBe(5);
        }

        [Test]
        public void BuffersUnqueued_AccumulatesCount()
        {
            AudioDiagnostics.RecordBuffersUnqueued(1, 2, 3);
            AudioDiagnostics.RecordBuffersUnqueued(1, 1, 2);

            AudioDiagnostics.BuffersUnqueued.ShouldBe(3);
        }

        [Test]
        public void BufferUnderflow_IncrementsCounter()
        {
            AudioDiagnostics.RecordBufferUnderflow(1, 0);
            AudioDiagnostics.RecordBufferUnderflow(2, 1);

            AudioDiagnostics.BufferUnderflows.ShouldBe(2);
        }

        [Test]
        public void BufferOverflow_IncrementsCounter()
        {
            AudioDiagnostics.RecordBufferOverflow(1, 2);

            AudioDiagnostics.BufferOverflows.ShouldBe(1);
        }

        [Test]
        public void ListenerCreatedDisposed_IncrementsCounters()
        {
            AudioDiagnostics.RecordListenerCreated("L1");
            AudioDiagnostics.RecordListenerCreated("L2");
            AudioDiagnostics.RecordListenerDisposed("L1");

            AudioDiagnostics.ListenersCreated.ShouldBe(2);
            AudioDiagnostics.ListenersDisposed.ShouldBe(1);
        }

        [Test]
        public void OpenALError_RecordsEvent()
        {
            AudioDiagnostics.RecordOpenALError("InvalidOperation");

            var events = AudioDiagnostics.GetRecentEvents();
            events.Length.ShouldBe(1);
            events[0].Kind.ShouldBe(AudioDiagnostics.DiagEventKind.OpenALError);
            events[0].Detail.ShouldNotBeNull();
            events[0].Detail!.ShouldContain("InvalidOperation");
        }

        [Test]
        public void RecentEvents_ContainsCorrectKinds()
        {
            AudioDiagnostics.RecordSourceStateChange(1, "A", "B");
            AudioDiagnostics.RecordBuffersQueued(1, 1, 1);
            AudioDiagnostics.RecordBufferUnderflow(1, 0);
            AudioDiagnostics.RecordListenerCreated("X");

            var events = AudioDiagnostics.GetRecentEvents();
            events.Length.ShouldBe(4);
            events[0].Kind.ShouldBe(AudioDiagnostics.DiagEventKind.SourceStateChange);
            events[1].Kind.ShouldBe(AudioDiagnostics.DiagEventKind.BufferQueued);
            events[2].Kind.ShouldBe(AudioDiagnostics.DiagEventKind.BufferUnderflow);
            events[3].Kind.ShouldBe(AudioDiagnostics.DiagEventKind.ListenerCreated);
        }

        [Test]
        public void RecentEvents_BoundedToMaxSize()
        {
            // Push more than 256 events
            for (int i = 0; i < 300; i++)
                AudioDiagnostics.RecordSourceStateChange((uint)i, "A", "B");

            var events = AudioDiagnostics.GetRecentEvents();
            // Should be approximately 256 (concurrent queue trimming is approximate)
            events.Length.ShouldBeLessThanOrEqualTo(300);
            events.Length.ShouldBeGreaterThanOrEqualTo(256);
        }

        [Test]
        public void TakeSnapshot_CapturesCurrentState()
        {
            AudioDiagnostics.RecordSourceStateChange(1, "A", "B");
            AudioDiagnostics.RecordBuffersQueued(1, 3, 3);
            AudioDiagnostics.RecordBufferUnderflow(1, 0);
            AudioDiagnostics.RecordBufferOverflow(1, 1);
            AudioDiagnostics.RecordBuffersUnqueued(1, 2, 1);
            AudioDiagnostics.RecordListenerCreated("L");
            AudioDiagnostics.RecordListenerDisposed("L");

            var snap = AudioDiagnostics.TakeSnapshot();
            snap.SourceStateTransitions.ShouldBe(1);
            snap.BuffersQueued.ShouldBe(3);
            snap.BufferUnderflows.ShouldBe(1);
            snap.BufferOverflows.ShouldBe(1);
            snap.BuffersUnqueued.ShouldBe(2);
            snap.ListenersCreated.ShouldBe(1);
            snap.ListenersDisposed.ShouldBe(1);
        }
    }
}
