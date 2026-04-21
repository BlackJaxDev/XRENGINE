using XREngine.Components;
using XREngine.Input;
using XREngine.Input.Devices;
using XREngine.Players;

namespace XREngine.Runtime.InputIntegration
{
    public abstract class PlayerController<T> : PlayerControllerBase where T : InputInterface
    {
        protected PlayerController(T input) : base()
        {
            _input = input;
            _input.InputRegistration += RegisterInput;
            _input.InputRegistration += RegisterControlledPawnInput;
        }

        private T _input;
        public T Input
        {
            get => _input;
            internal set => SetField(ref _input, value);
        }

        /// <inheritdoc />
        public override object? InputDevice => _input;

        protected override bool OnPropertyChanging<T2>(string? propName, T2 field, T2 @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(Input):
                        _input.InputRegistration -= RegisterInput;
                        _input.InputRegistration -= RegisterControlledPawnInput;
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
                    _input.InputRegistration += RegisterControlledPawnInput;
                    break;
                case nameof(ControlledPawn):
                    if (_controlledPawn is not null)
                        RegisterController(_controlledPawn);
                    break;
            }
            base.OnPropertyChanged(propName, prev, field);
        }

        protected void RegisterController(XRComponent c)
        {
            if (Input is null)
                return;

            //Run registration for the input interface
            Input.TryRegisterInput();

            if (PlayerInfo.LocalIndex is not null)
                System.Diagnostics.Debug.WriteLine($"Local player {PlayerInfo.LocalIndex} gained control of {_controlledPawn}");
            else
                System.Diagnostics.Debug.WriteLine($"Server player {PlayerInfo.ServerIndex} gained control of {_controlledPawn}");
        }

        private void RegisterControlledPawnInput(InputInterface input)
        {
            if (_controlledPawn is IRuntimeInputControllablePawn controllablePawn)
                controllablePawn.RegisterControllerInput(input);
        }

        private void UnregisterController(XRComponent c)
        {
            if (Input is null)
                return;

            //Unregister inputs for the controlled pawn
            Input.TryUnregisterInput();

            if (PlayerInfo.LocalIndex is not null)
                System.Diagnostics.Debug.WriteLine($"Local player {PlayerInfo.LocalIndex} is releasing control of {_controlledPawn}");
            else
                System.Diagnostics.Debug.WriteLine($"Server player {PlayerInfo.ServerIndex} is releasing control of {_controlledPawn}");
        }

        protected abstract void RegisterInput(InputInterface input);

        protected override void OnDestroying()
        {
            base.OnDestroying();
            _input.InputRegistration -= RegisterInput;
            _input.InputRegistration -= RegisterControlledPawnInput;
        }
    }
}
