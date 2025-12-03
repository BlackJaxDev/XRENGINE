using Silk.NET.OpenAL;
using System.ComponentModel;
using System.Numerics;
using XREngine.Audio;

namespace XREngine.Components
{
    [Category("Audio")]
    [DisplayName("Audio Listener")]
    [Description("Defines the listener position, orientation, and audio settings used for spatial mixing.")]
    public class AudioListenerComponent : XRComponent
    {
        [Browsable(false)]
        public ListenerContext? Listener { get; private set; }

        /// <summary>
        /// Exaggeration factor for Doppler effect.
        /// </summary>
        [Category("Audio Settings")]
        [DisplayName("Doppler Factor")]
        [Description("Multiplier for Doppler pitch shift.")]
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
        [Category("Audio Settings")]
        [DisplayName("Speed Of Sound")]
        [Description("Speed of sound in units per second (default ~343 m/s).")]
        public float SpeedOfSound
        {
            get => Listener?.SpeedOfSound ?? 343.3f;
            set
            {
                if (Listener is not null)
                    Listener.SpeedOfSound = value;
            }
        }
        /// <summary>
        /// Algorithm used for attenuation.
        /// </summary>
        [Category("Audio Settings")]
        [DisplayName("Distance Model")]
        [Description("Attenuation model used for distance calculations.")]
        public DistanceModel DistanceModel
        {
            get => Listener?.DistanceModel ?? DistanceModel.None;
            set
            {
                if (Listener is not null)
                    Listener.DistanceModel = value;
            }
        }
        /// <summary>
        /// Master gain for the listener.
        /// </summary>
        [Category("Volume")]
        [DisplayName("Gain")]
        [Description("Master volume for all audio heard by this listener.")]
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
