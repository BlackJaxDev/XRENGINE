namespace XREngine.Input.Devices
{
    public delegate void DelSendAxisValue(int axisIndex, bool continuous, float value);
    public delegate void DelAxisValue(float value);
    [Serializable]
    public class AxisManager(int index, string name) : ButtonManager(index, name)
    {
        public event DelSendAxisValue? ListExecuted;

        protected List<DelAxisValue> _continuousUpdate = [];
        protected List<DelAxisValue> _deltaUpdate = [];
        
        private float _value = 0.0f;
        public float Value => Math.Abs(_value) > DeadZoneThreshold ? _value : 0.0f;

        public float PressedThreshold { get; set; } = 0.9f;
        public float DeadZoneThreshold { get; set; } = 0.1f;
        public float UpdateThreshold { get; set; } = 0.0001f;

        #region Registration
        public override bool IsEmpty() => base.IsEmpty() && _continuousUpdate.Count == 0 && _deltaUpdate.Count == 0;
        public void RegisterAxis(DelAxisValue func, bool continuousUpdate, bool unregister)
        {
            if (unregister)
            {
                if (continuousUpdate)
                    _continuousUpdate.Remove(func);
                else
                    _deltaUpdate.Remove(func);
            }
            else
            {
                if (continuousUpdate)
                    _continuousUpdate.Add(func);
                else
                    _deltaUpdate.Add(func);
            }
        }
        public override void UnregisterAll()
        {
            base.UnregisterAll();
            _continuousUpdate.Clear();
            _deltaUpdate.Clear();
        }
        #endregion

        #region Actions
        internal void Tick(float value, float delta)
        {
            float prev = Value;
            _value = value;
            float realValue = Value;

            OnContinuousAxisUpdate(realValue);
            if (Math.Abs(realValue - prev) > UpdateThreshold)
                OnAxisChanged(realValue);

            //Tick button events using a pressed threshold so the axis can also be used as a button
            Tick(Math.Abs(realValue) > PressedThreshold, delta);
        }

        private void OnAxisChanged(float value)
            => ExecuteList(false, value);

        private void OnContinuousAxisUpdate(float value)
            => ExecuteList(true, value);

        private void ExecuteList(bool continuous, float value)
        {
            List<DelAxisValue> list = continuous ? _continuousUpdate : _deltaUpdate;
            ListExecuted?.Invoke(Index, continuous, value);
            foreach (DelAxisValue v in list)
                v?.Invoke(value);
        }
        #endregion

        public override string ToString() => Name;
    }
}
