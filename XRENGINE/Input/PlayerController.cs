using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Players;

namespace XREngine.Input
{
    public abstract class PlayerController<T> : PlayerControllerBase where T : InputInterface
    {
        protected PlayerController(T input) : base()
        {
            _input = input;
            _input.InputRegistration += RegisterInput;
        }

        private T _input;
        public T Input
        {
            get => _input;
            internal set => SetField(ref _input, value);
        }

        protected override bool OnPropertyChanging<T2>(string? propName, T2 field, T2 @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Input):
                        _input.InputRegistration -= RegisterInput;
                        break;
                    case nameof(ControlledPawn):
                        if (_controlledPawn is not null)
                            UnregisterController(_controlledPawn);
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T2>(string? propName, T2 prev, T2 field)
        {
            switch (propName)
            {
                case nameof(Input):
                    _input.InputRegistration += RegisterInput;
                    break;
                case nameof(ControlledPawn):
                    if (_controlledPawn is not null)
                        RegisterController(_controlledPawn);
                    break;
            }
            base.OnPropertyChanged(propName, prev, field);
        }

        protected void RegisterController(PawnComponent c)
        {
            if (Input is null)
                return;

            RegisterInputEvents(c);

            //Run registration for the input interface
            Input.TryRegisterInput();

            if (PlayerInfo.LocalIndex is not null)
                Debug.Out($"Local player {PlayerInfo.LocalIndex} gained control of {_controlledPawn}");
            else
                Debug.Out($"Server player {PlayerInfo.ServerIndex} gained control of {_controlledPawn}");
        }

        protected virtual void RegisterInputEvents(PawnComponent c)
        {
            //Tell this controller to register input for the controlled pawn
            Input.InputRegistration += c.RegisterInput;
            Input.InputRegistration += c.RegisterOptionalInputs;

            //If the controlled pawn has a user interface input, register input for that as well
            if (c.UserInterfaceInput != null)
                Input.InputRegistration += c.UserInterfaceInput.RegisterInput;
        }

        private void UnregisterController(PawnComponent c)
        {
            if (Input is null)
                return;

            //Unregister inputs for the controlled pawn
            Input.TryUnregisterInput();

            UnregisterInputEvents(c);

            if (PlayerInfo.LocalIndex is not null)
                Debug.Out($"Local player {PlayerInfo.LocalIndex} is releasing control of {_controlledPawn}");
            else
                Debug.Out($"Server player {PlayerInfo.ServerIndex} is releasing control of {_controlledPawn}");
        }

        protected virtual void UnregisterInputEvents(PawnComponent c)
        {
            //Unregister inputs for the controlled pawn
            Input.InputRegistration -= c.RegisterInput;
            Input.InputRegistration -= c.RegisterOptionalInputs;

            //If the controlled pawn has a user interface input, unregister input for that as well
            if (c.UserInterfaceInput != null)
                Input.InputRegistration -= c.UserInterfaceInput.RegisterInput;
        }

        protected abstract void RegisterInput(InputInterface input);

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _input.InputRegistration -= RegisterInput;
        }
    }
}
