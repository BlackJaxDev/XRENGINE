using Extensions;
using XREngine.Animation;
using XREngine.Data;
using XREngine.Scene.Components.Animation;

namespace XREngine.Components
{
    public class BlendManager
    {
        private class BlendInfo
        {
            /// <summary>
            /// How long the blend lasts in seconds.
            /// </summary>
            public float Duration
            {
                get => _invDuration == 0.0f ? 0.0f : 1.0f / _invDuration;
                set => _invDuration = value == 0.0f ? 0.0f : 1.0f / value;
            }
            /// <summary>
            /// How long until the blend is done in seconds.
            /// </summary>
            public float ElapsedTime { get; private set; }
            /// <summary>
            /// The blending method to use.
            /// </summary>
            public EAnimBlendType Type
            {
                get => _blendType;
                set
                {
                    _blendType = value;
                    _blendFunction = _blendType switch
                    {
                        EAnimBlendType.CosineEaseInOut => (time) => Interp.Cosine(0.0f, 1.0f, time),
                        EAnimBlendType.QuadraticEaseStart => (time) => Interp.QuadraticEaseStart(0.0f, 1.0f, time),
                        EAnimBlendType.QuadraticEaseEnd => (time) => Interp.QuadraticEaseEnd(0.0f, 1.0f, time),
                        EAnimBlendType.Custom => (time) => _customBlendFunction.GetValue(time),
                        _ => (time) => time,
                    };
                }
            }

            private AnimStateTransition _transition;
            private float _invDuration;
            private EAnimBlendType _blendType;
            private Func<float, float> _blendFunction;
            private PropAnimFloat? _customBlendFunction;

            public BlendInfo() { }
            public BlendInfo(AnimStateTransition transition)
            {
                _transition = transition;
                Duration = transition.BlendDuration;
                ElapsedTime = 0.0f;
                Type = transition.BlendType;
                _customBlendFunction = transition.CustomBlendFunction;
            }

            /// <summary>
            /// Increments the blending time.
            /// Returns true if the blend is done.
            /// </summary>
            internal bool Tick(float delta)
                => (ElapsedTime += delta) > Duration;

            //Multiplying is faster than division, store duration as inverse
            /// <summary>
            /// Returns a value from 0.0f - 1.0f indicating a linear time between two animations.
            /// </summary>
            public float GetBlendTime() => _blendFunction(_invDuration == 0.0f ? 1.0f : (ElapsedTime * _invDuration).ClampMax(1.0f));

            internal void OnStarted()
            {
                _transition?.OnStarted();
            }
            internal void OnFinished()
            {
                _transition?.OnFinished();
            }
        }

        private LinkedList<AnimState> _stateQueue;
        private LinkedList<BlendInfo> _blendQueue;

        public AnimState? LastState => _stateQueue?.Last?.Value;

        public BlendManager(AnimState initialState)
        {
            _blendQueue = new LinkedList<BlendInfo>();
            _stateQueue = new LinkedList<AnimState>();
            if (initialState != null)
            {
                _stateQueue.AddFirst(initialState);
                //initialState.Start();
            }
        }

        public void QueueState(
            AnimState destinationState,
            AnimStateTransition transition)
        {
            if (destinationState is null)
                return;

            //destinationState.Start();
            _stateQueue.AddLast(destinationState);
            if (_stateQueue.Count > 1)
            {
                BlendInfo blend = new(transition);
                blend.OnStarted();
                _blendQueue.AddLast(blend);
            }
        }

        public void Tick(float delta, EventList<AnimState> states, HumanoidComponent skeleton)
        {
            AnimStateTransition? transition = LastState?.TryTransition();
            int index = transition?.DestinationStateIndex ?? -1;

            if (states.IndexInRange(index))
                QueueState(states[index], transition);

            Tick(delta, skeleton);
        }

        private void Tick(float delta, HumanoidComponent skeleton)
        {
            if (skeleton is null)
                return;

            LinkedListNode<AnimState>? stateNode = _stateQueue.First;

            //Tick first animation
            AnimState? anim = stateNode?.Value;
            HumanoidPose? baseFrame = null;
            if (anim != null)
            {
                baseFrame = anim.GetFrame();
                anim.Tick(delta);
            }

            if (baseFrame is null)
                return;

            if (_blendQueue.Count > 0)
            {
                //Execute all blends on top of the first animation's frame
                var blendNode = _blendQueue.First;
                while (blendNode != null)
                {
                    //Get the blend info for the current animation and the next
                    BlendInfo blend = blendNode.Value;

                    //Tick the animation to be blended with next
                    stateNode = stateNode?.Next;
                    anim = stateNode?.Value;
                    if (anim != null)
                    {
                        //Update blending information
                        if (blend.Tick(delta))
                        {
                            //Frame is now entirely the next animation.
                            baseFrame = anim.GetFrame();

                            //All previous animations are now irrelevant. Remove them
                            while (_stateQueue.First != stateNode)
                            {
                                _stateQueue.First?.Value.OnEnded();
                                _stateQueue.RemoveFirst();
                            }
                            //Remove all previous blends
                            while (_blendQueue.First != blendNode)
                            {
                                _blendQueue.First?.Value.OnFinished();
                                _blendQueue.RemoveFirst();
                            }
                            //And this one
                            _blendQueue.First.Value.OnFinished();
                            _blendQueue.RemoveFirst();

                            //Get next blend info
                            blendNode = _blendQueue.First;
                        }
                        else
                        {
                            //Update frame by blending it with the next animation using the current blending time
                            baseFrame?.BlendWith(stateNode?.Value?.GetFrame(), blend.GetBlendTime());

                            //Increment to next blend info
                            blendNode = blendNode.Next;
                        }

                        anim.Tick(delta);
                    }
                }
            }

            //Update the skeleton with the final blended frame
            baseFrame.UpdateSkeleton(skeleton);
        }
    }
}
