using Silk.NET.OpenGL;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Fixed ownership and fence-retirement store for native Advanced outputs.
/// Slots never overwrite a publication until its GL fence has completed.
/// </summary>
internal sealed class OpenGLAdvancedVisibilityOutputRegistry : IDisposable
{
    internal const int BankCapacity = 8;
    private const int FramesPerBank = 3;
    private readonly OpenGLRenderer _renderer;
    private readonly Bank[] _banks = new Bank[BankCapacity];
    private long _nextIncarnation;

    internal OpenGLAdvancedVisibilityOutputRegistry(OpenGLRenderer renderer)
    {
        _renderer = renderer;
        for (int index = 0; index < _banks.Length; ++index)
            _banks[index] = new Bank(checked((ulong)index + 1));
    }

    internal bool TryReserve(ulong outputId, long generation, out AdvancedVisibilityFamilyReservation reservation, out string reason)
    {
        PollCompletedPublications();
        reservation = default;
        if (outputId == 0 || generation <= 0)
        {
            reason = "The OpenGL Advanced output identity is invalid.";
            return false;
        }
        foreach (Bank bank in _banks)
            if (bank.Active && bank.OutputId == outputId && bank.Generation == generation)
            {
                reservation = bank.Reservation;
                reason = "Ready";
                return true;
            }
        foreach (Bank bank in _banks)
            if (!bank.Active && bank.TryFinalize(_renderer))
            {
                long incarnation = checked(++_nextIncarnation);
                if (incarnation <= 0)
                {
                    reason = "OpenGL Advanced output-bank incarnation exhausted.";
                    return false;
                }
                bank.Activate(outputId, generation, incarnation);
                reservation = bank.Reservation;
                reason = "Ready";
                return true;
            }
        reason = "All eight OpenGL Advanced output banks are active or await GPU fence completion.";
        return false;
    }

    internal bool TryAcquireSlot(in AdvancedVisibilityFamilyReservation reservation, out OpenGLAdvancedVisibilitySlot? slot, out string reason)
    {
        PollCompletedPublications();
        slot = null;
        foreach (Bank bank in _banks)
            if (bank.Reservation == reservation && bank.Active)
                return bank.TryAcquire(_renderer, out slot, out reason);
        reason = "The OpenGL Advanced output reservation is stale or retiring.";
        return false;
    }

    internal bool TryGetRetainedSlot(in AdvancedVisibilityFamilyReservation reservation, out OpenGLAdvancedVisibilitySlot? slot)
    {
        foreach (Bank bank in _banks)
            if (bank.Reservation == reservation && bank.Active)
            {
                slot = bank.RetainedSlot;
                return slot is not null;
            }
        slot = null;
        return false;
    }

    internal void Retire(in AdvancedVisibilityFamilyReservation reservation)
    {
        foreach (Bank bank in _banks)
            if (bank.Reservation == reservation)
                bank.Active = false;
    }

    internal bool IsCurrent(in AdvancedVisibilityFamilyReservation reservation)
    {
        foreach (Bank bank in _banks)
            if (bank.Active && bank.Reservation == reservation) return true;
        return false;
    }

    internal void Complete(in AdvancedVisibilityFamilyReservation reservation)
    {
        foreach (Bank bank in _banks)
            if (bank.Reservation == reservation) bank.RetainedSlot = null;
    }

    /// <summary>Retires completion-owned scene leases even for idle or retired
    /// outputs. Publication backpressure must not prevent its own recovery.</summary>
    internal void PollCompletedPublications()
    {
        foreach (Bank bank in _banks)
            foreach (OpenGLAdvancedVisibilitySlot slot in bank.Slots)
                if (!ReferenceEquals(slot, bank.RetainedSlot))
                    slot.TryRetireCompletedPublication(_renderer);
    }

    internal unsafe bool TryBindPersistentState(in AdvancedVisibilityFamilyReservation reservation, uint byteCount, out string reason)
    {
        byteCount = Math.Max(sizeof(uint), byteCount);
        foreach (Bank bank in _banks)
        {
            if (!bank.Active || bank.Reservation != reservation)
                continue;
            if (bank.PersistentBuffer == 0)
                bank.PersistentBuffer = _renderer.RawGL.CreateBuffer();
            if (bank.PersistentBuffer == 0 || byteCount == 0)
            {
                reason = "OpenGL Advanced persistent-state allocation failed.";
                return false;
            }
            if (bank.PersistentBytes < byteCount || bank.PersistentNeedsReset)
            {
                bank.PersistentBytes = Math.Max(bank.PersistentBytes, byteCount);
                _renderer.RawGL.NamedBufferData(bank.PersistentBuffer, bank.PersistentBytes, null, GLEnum.DynamicDraw);
                uint zero = 0;
                _renderer.RawGL.ClearNamedBufferData(bank.PersistentBuffer, GLEnum.R32ui, GLEnum.RedInteger, GLEnum.UnsignedInt, &zero);
                bank.PersistentNeedsReset = false;
            }
            _renderer.RawGL.BindBufferBase(GLEnum.ShaderStorageBuffer, 49u, bank.PersistentBuffer);
            reason = "Ready";
            return true;
        }
        reason = "OpenGL Advanced persistent state belongs to a stale reservation.";
        return false;
    }

    public void Dispose()
    {
        foreach (Bank bank in _banks)
            bank.Dispose(_renderer);
    }

    private sealed class Bank
    {
        private readonly ulong _bankId;
        internal Bank(ulong bankId) => _bankId = bankId;
        internal uint PersistentBuffer;
        internal uint PersistentBytes;
        internal bool PersistentNeedsReset;
        internal readonly OpenGLAdvancedVisibilitySlot[] Slots = [new(), new(), new()];
        internal AdvancedVisibilityFamilyReservation Reservation;
        internal ulong OutputId;
        internal long Generation;
        internal bool Active;
        private int _nextSlot;
        internal OpenGLAdvancedVisibilitySlot? RetainedSlot;

        internal void Activate(ulong outputId, long generation, long incarnation)
        {
            OutputId = outputId; Generation = generation; Active = true;
            Reservation = new(generation, outputId, _bankId, incarnation);
            PersistentNeedsReset = true;
        }

        internal bool TryAcquire(OpenGLRenderer renderer, out OpenGLAdvancedVisibilitySlot? slot, out string reason)
        {
            if (RetainedSlot is not null)
            {
                slot = null;
                reason = "An unfinished OpenGL Advanced family still owns this output bank.";
                return false;
            }
            for (int attempt = 0; attempt < FramesPerBank; ++attempt)
            {
                OpenGLAdvancedVisibilitySlot candidate = Slots[_nextSlot];
                _nextSlot = (_nextSlot + 1) % FramesPerBank;
                if (candidate.TryAcquire(renderer))
                {
                    RetainedSlot = candidate;
                    slot = candidate; reason = "Ready"; return true;
                }
            }
            slot = null; reason = "Every OpenGL Advanced frame slot is still GPU-owned."; return false;
        }

        internal bool TryFinalize(OpenGLRenderer renderer)
        {
            if (RetainedSlot is not null) return false;
            foreach (OpenGLAdvancedVisibilitySlot slot in Slots)
                if (!slot.TryRetireCompletedPublication(renderer)) return false;
            return true;
        }

        internal void Dispose(OpenGLRenderer renderer)
        {
            foreach (OpenGLAdvancedVisibilitySlot slot in Slots) slot.Dispose(renderer);
            if (PersistentBuffer != 0) renderer.RawGL.DeleteBuffer(PersistentBuffer);
            PersistentBuffer = 0;
            RetainedSlot = null;
        }
    }
}
