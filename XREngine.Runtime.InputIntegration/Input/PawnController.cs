using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Networking;
using XREngine.Players;
using XREngine.Scene;

namespace XREngine.Input
{
    /// <summary>
    /// This base class is used to send input information to a movement component for an actor.
    /// Input can come from a local or server player, being an actual person or an AI (these are subclasses to pawn controller).
    /// </summary>
    public abstract class PawnController : XRObjectBase, IPawnController
    {
        /// <inheritdoc />
        public virtual bool IsLocal => false;

        /// <inheritdoc />
        public virtual PlayerInfo? PlayerInfo => null;

        /// <inheritdoc />
        public virtual void TickPawnInput(float delta, bool isUIInputCaptured) { }

        /// <inheritdoc />
        public virtual void OnPawnCameraChanged() { }

        /// <inheritdoc />
        public virtual object? InputDevice => null;

        // --- Viewport (explicit interface → virtual dispatch) ---
        object? IPawnController.Viewport { get => GetViewportCore(); set => SetViewportCore(value); }
        protected virtual object? GetViewportCore() => null;
        protected virtual void SetViewportCore(object? value) { }

        // --- FocusedInteractable (explicit interface → virtual dispatch) ---
        object? IPawnController.FocusedInteractable { get => GetFocusedInteractableCore(); set => SetFocusedInteractableCore(value); }
        protected virtual object? GetFocusedInteractableCore() => null;
        protected virtual void SetFocusedInteractableCore(object? value) { }

        // --- ControlledPawnComponent (explicit interface → delegates to ControlledPawn) ---
        XRComponent? IPawnController.ControlledPawnComponent
        {
            get => ControlledPawn;
            set => ControlledPawn = value as PawnComponent;
        }

        // --- EnqueuePossession (explicit interface → delegates to EnqueuePosession) ---
        void IPawnController.EnqueuePossession(XRComponent pawn)
        {
            if (pawn is PawnComponent pc)
                EnqueuePosession(pc);
        }

        // --- LocalPlayerIndex (explicit interface → virtual dispatch) ---
        ELocalPlayerIndex? IPawnController.LocalPlayerIndex => GetLocalPlayerIndexCore();
        protected virtual ELocalPlayerIndex? GetLocalPlayerIndexCore() => null;

        // --- ApplyNetworkTransform (virtual, overridden by RemotePlayerController) ---
        public virtual void ApplyNetworkTransform(PlayerTransformUpdate update) { }
        //TODO: gamemode vs pawncontroller possession queue usage?
        protected readonly Queue<PawnComponent> _pawnPossessionQueue = new();

        protected PawnComponent? _controlledPawn;
        public virtual PawnComponent? ControlledPawn
        {
            get => _controlledPawn;
            set => SetField(ref _controlledPawn, value is null && _pawnPossessionQueue.Count > 0 ? _pawnPossessionQueue.Dequeue() : value);
        }

        //protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        //{
        //    bool change = base.OnPropertyChanging(propName, field, @new);
        //    if (change)
        //    {
        //        switch (propName)
        //        {
        //            case nameof(ControlledPawn):
        //                //if (ControlledPawn?.Controller == this)
        //                //    ControlledPawn.Controller = null;
        //                break;
        //        }
        //    }
        //    return change;
        //}
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ControlledPawn):
                    ControlledPawn?.Controller = this;
                    break;
            }
        }

        /// <summary>
        /// Queues the given pawn for possession.
        /// If the currently possessed pawn is null, possesses the given pawn immediately.
        /// </summary>
        /// <param name="pawn">The pawn to possess.</param>
        public void EnqueuePosession(PawnComponent pawn)
        {
            if (ControlledPawn is null)
                ControlledPawn = pawn;
            else
                _pawnPossessionQueue.Enqueue(pawn);
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            UnlinkControlledPawn();
        }

        public void UnlinkControlledPawn()
        {
            _pawnPossessionQueue.Clear();
            ControlledPawn = null;
        }
    }
}
