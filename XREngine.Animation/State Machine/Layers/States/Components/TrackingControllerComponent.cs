namespace XREngine.Animation
{
    public class TrackingControllerComponent : AnimStateComponent
    {
        public enum ETrackingMode
        {
            Unchanged,
            Tracking,
            Animation,
        }

        public ETrackingMode? TrackingModeAll
        {
            get => 
                TrackingModeHead == TrackingModeLeftHand && 
                TrackingModeLeftHand == TrackingModeRightHand &&
                TrackingModeRightHand == TrackingModeLeftFoot &&
                TrackingModeLeftFoot == TrackingModeRightFoot &&
                TrackingModeRightFoot == TrackingModeLeftFingers &&
                TrackingModeLeftFingers == TrackingModeRightFingers &&
                TrackingModeRightFingers == TrackingModeEyes &&
                TrackingModeEyes == TrackingModeMouth
                ? (ETrackingMode?)TrackingModeHead
                : null;
            set
            {
                if (value is null)
                    return;
                TrackingModeHead = value.Value;
                TrackingModeLeftHand = value.Value;
                TrackingModeRightHand = value.Value;
                TrackingModeLeftFoot = value.Value;
                TrackingModeRightFoot = value.Value;
                TrackingModeLeftFingers = value.Value;
                TrackingModeRightFingers = value.Value;
                TrackingModeEyes = value.Value;
                TrackingModeMouth = value.Value;
            }
        }

        private ETrackingMode _trackingModeHead = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeHead
        {
            get => _trackingModeHead;
            set => SetField(ref _trackingModeHead, value);
        }

        private ETrackingMode _trackingModeLeftHand = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeLeftHand
        {
            get => _trackingModeLeftHand;
            set => SetField(ref _trackingModeLeftHand, value);
        }

        private ETrackingMode _trackingModeRightHand = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeRightHand
        {
            get => _trackingModeRightHand;
            set => SetField(ref _trackingModeRightHand, value);
        }

        private ETrackingMode _trackingModeLeftFoot = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeLeftFoot
        {
            get => _trackingModeLeftFoot;
            set => SetField(ref _trackingModeLeftFoot, value);
        }

        private ETrackingMode _trackingModeRightFoot = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeRightFoot
        {
            get => _trackingModeRightFoot;
            set => SetField(ref _trackingModeRightFoot, value);
        }

        private ETrackingMode _trackingModeLeftFingers = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeLeftFingers
        {
            get => _trackingModeLeftFingers;
            set => SetField(ref _trackingModeLeftFingers, value);
        }

        private ETrackingMode _trackingModeRightFingers = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeRightFingers
        {
            get => _trackingModeRightFingers;
            set => SetField(ref _trackingModeRightFingers, value);
        }

        private ETrackingMode _trackingModeEyes = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeEyes
        {
            get => _trackingModeEyes;
            set => SetField(ref _trackingModeEyes, value);
        }

        private ETrackingMode _trackingModeMouth = ETrackingMode.Unchanged;
        public ETrackingMode TrackingModeMouth
        {
            get => _trackingModeMouth;
            set => SetField(ref _trackingModeMouth, value);
        }
    }
}
