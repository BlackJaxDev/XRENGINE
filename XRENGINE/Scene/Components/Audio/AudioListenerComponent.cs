using Silk.NET.OpenAL;
using System.Numerics;
using XREngine.Audio;

namespace XREngine.Components
{
    public class AudioListenerComponent : XRComponent
    {
        public ListenerContext? Listener { get; private set; }

        public float DopplerFactor
        {
            get => Listener?.DopplerFactor ?? 1.0f;
            set
            {
                if (Listener is not null) 
                    Listener.DopplerFactor = value;
            }
        }
        /// <summary>
        /// Speed of Sound in units per second. Default: 343.3f.
        /// </summary>
        public float SpeedOfSound
        {
            get => Listener?.SpeedOfSound ?? 343.3f;
            set
            {
                if (Listener is not null)
                    Listener.SpeedOfSound = value;
            }
        }
        public DistanceModel DistanceModel
        {
            get => Listener?.DistanceModel ?? DistanceModel.None;
            set
            {
                if (Listener is not null)
                    Listener.DistanceModel = value;
            }
        }
        public float Gain
        {
            get => Listener?.Gain ?? 1.0f;
            set
            {
                if (Listener is not null)
                    Listener.Gain = value;
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            MakeListener();
            RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdatePosition);
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            DestroyListener();
        }

        private void MakeListener()
        {
            if (Listener is not null)
                return;

            Listener = Engine.Audio.NewListener(Name);
            World?.Listeners?.Add(Listener);
        }

        private void DestroyListener()
        {
            if (Listener is not null)
                World?.Listeners?.Remove(Listener);

            Listener?.Dispose();
            Listener = null;
        }

        private void UpdatePosition()
        {
            if (Listener is null)
                return;

            float delta = Engine.Delta;
            Vector3 pos = Transform.WorldTranslation;

            UpdateListenerPosition(pos, delta);
        }

        private void UpdateListenerPosition(Vector3 pos, float delta)
        {
            if (Listener is null)
                return;

            Listener.Velocity = delta > 0.0f ? (pos - Listener.Position) / delta : Vector3.Zero;
            Listener.Position = pos;
            Listener.SetOrientation(Transform.WorldForward, Transform.WorldUp);
        }
    }
}
