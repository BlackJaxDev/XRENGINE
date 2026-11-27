using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OscCore;
using OscCore.OscQuery;
using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;

namespace XREngine.Data.Components
{
    /// <summary>
    /// Dynamically binds every <see cref="AnimFloat"/>, <see cref="AnimBool"/>, and
    /// <see cref="AnimInt"/> parameter on a target <see cref="AnimStateMachineComponent"/> to
    /// incoming OSC addresses, applies incoming values on the main thread with zero per-packet
    /// heap allocations, rebinds automatically when <c>ParameterSchemaVersion</c> changes, and
    /// optionally advertises the parameter tree over OSCQuery.
    /// </summary>
    public class AnimStateMachineOscReceiverComponent : OscReceiverComponent
    {
        /// <summary>Default OSC parameter address prefix used by VRChat and VRCFT.</summary>
        public const string DefaultAvatarParameterPrefix = "/avatar/parameters/";

        /// <summary>Standard OSC address used to advertise avatar schema changes.</summary>
        public const string AvatarChangeAddress = "/avatar/change";

        /// <summary>Standard OSCQuery address carrying the active avatar display name.</summary>
        public const string AvatarNameAddress = "/avatar/name";

        /// <summary>Standard OSCQuery address indicating native full-face-tracking mode.</summary>
        public const string AvatarFullFaceTrackingAddress = "/avatar/avatarfullfacetracking";

        /// <summary>Default OSCQuery service name discovered by VRCFT v5+.</summary>
        public const string DefaultOscQueryServiceName = "VRChat-Client-XRENGINE";

        /// <summary>Default VRChat-formatted avatar identifier used when no avatar seed is provided.</summary>
        public const string DefaultAvatarId = "avtr_00000000-0000-0000-0000-000000000000";

        private static readonly long OscQueryNotifyMinIntervalTicks =
            Math.Max(1L, (long)Math.Round(1.5 * Stopwatch.Frequency));

        private const float DefaultSignalLossTimeoutSeconds = 2.5f;
        private const float DefaultNeutralResetDurationSeconds = 0.35f;
        private const float NeutralCommitDurationSeconds = 0.20f;
        private const int NeutralCommitMinFrames = 4;
        private const float NeutralFloatSnapEpsilon = 0.002f;

        // VRCFaceTracking defines these tracking-state flags as ConditionalBoolParameter (bool),
        // while Unity Direct Blend Trees require float animator parameters. Advertising them as
        // OSCQuery bool (T/F) lets VRCFT match param.Type == typeof(bool) and send updates.
        private static readonly HashSet<string> StandardOscBoolParameters = new(StringComparer.Ordinal)
        {
            "EyeTrackingActive",
            "LipTrackingActive",
            "ExpressionTrackingActive",
        };

        // Local template configuration parameters that must not be zeroed during neutral pose reset.
        private static readonly HashSet<string> LocalConfigurationParameters = new(StringComparer.Ordinal)
        {
            "IsLocal",
            "FT/DirectBlend",
            "VisemesEnable",
            "FaceTrackingEmulation",
            "FaceTrackingLimits",
        };

        private readonly List<BoundParameterSlot> _boundSlots = [];
        private readonly HashSet<string> _boundAddresses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OscQueryValueKind> _oscQueryParameterTypeOverrides = new(StringComparer.Ordinal);

        private AnimStateMachineComponent? _animStateMachine;
        private OscSenderComponent? _outboundSender;
        private HumanoidProceduralEyeGazeComponent? _proceduralEyeGaze;
        private HumanoidProceduralEyeGazeComponent? _cachedSiblingEyeGaze;
        private EFaceTrackingControlMode _controlMode = EFaceTrackingControlMode.Auto;
        private float _signalLossTimeoutSeconds = DefaultSignalLossTimeoutSeconds;
        private float _neutralResetDurationSeconds = DefaultNeutralResetDurationSeconds;
        private bool _enableDynamicParameterBinding = true;
        private bool _enableOscQuery = true;
        private string _oscQueryServiceName = DefaultOscQueryServiceName;
        private int _oscQueryHttpPort;
        private string _avatarId = DefaultAvatarId;
        private string _avatarName = string.Empty;

        private OscQueryServer? _oscQueryServer;
        private AnimStateMachine? _boundStateMachine;
        private int _boundSchemaVersion = -1;
        private string _boundPrefix = string.Empty;
        private long _receivedParameterPacketCount;
        private long _avatarChangeSendCount;
        private long _lastAvatarChangeTimestamp;
        private long _lastTrackingPacketTimestamp;
        private long _lastExplicitDisableTimestamp;
        private long _lastArbitrationTickTimestamp;
        private int _explicitEyeTrackingActive = -1;
        private int _explicitLipTrackingActive = -1;
        private int _explicitExpressionTrackingActive = -1;
        private int _pendingOscQueryAvatarChangeNotify;
        private int _pendingPeerAvatarChangeNotify;
        private string? _lastNotifiedAvatarId;
        private int _lastNotifiedSchemaVersion = -1;
        private int _lastNotifiedBoundCount = -1;
        private bool _schemaTickRegistered;
        private bool _isTrackingSignalLive;
        private bool _isFaceTrackingDrivingAvatar;
        private bool _isRelaxingToNeutral;
        private bool _isProceduralFallbackActive;
        private bool _animatorSuspendedByReceiver;
        private bool _needsNeutralCommit;
        private float _neutralCommitRemainingSeconds;
        private int _neutralCommitFramesRemaining;

        /// <summary>Initializes the receiver with the standard avatar parameter prefix.</summary>
        public AnimStateMachineOscReceiverComponent()
        {
            ParameterPrefix = DefaultAvatarParameterPrefix;
        }

        /// <summary>
        /// Derives a deterministic VRChat-compatible <c>avtr_&lt;guid&gt;</c> identifier from
        /// <paramref name="seed"/> so external OSC listeners such as VRCFT accept the avatar ID
        /// format consistently across runs.
        /// </summary>
        public static string CreateDeterministicAvatarId(string? seed)
        {
            if (string.IsNullOrWhiteSpace(seed))
                return DefaultAvatarId;

            string trimmed = seed.Trim();
            if (trimmed.StartsWith("avtr_", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(trimmed.AsSpan(5), out Guid existingGuid))
                return $"avtr_{existingGuid:D}";

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"xrengine:osc-avatar:{trimmed}"));
            Span<byte> guidBytes = stackalloc byte[16];
            hash.AsSpan(0, 16).CopyTo(guidBytes);
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
            return $"avtr_{new Guid(guidBytes):D}";
        }

        /// <summary>
        /// Gets or sets the target <see cref="AnimStateMachineComponent"/>. When <see langword="null"/>,
        /// resolves the sibling component on the same scene node.
        /// </summary>
        public AnimStateMachineComponent? AnimStateMachine
        {
            get => _animStateMachine;
            set
            {
                if (!SetField(ref _animStateMachine, value))
                    return;
                if (Server is not null)
                    SyncDynamicSchema(forceRebind: true);
            }
        }

        /// <summary>
        /// Gets or sets an optional <see cref="OscSenderComponent"/> used to emit
        /// <c>/avatar/change</c> notifications when the bound schema changes.
        /// </summary>
        public OscSenderComponent? OutboundSender
        {
            get => _outboundSender;
            set
            {
                if (!SetField(ref _outboundSender, value))
                    return;
                if (Server is not null && _boundStateMachine is not null)
                    TryNotifyAvatarChanged(force: true);
            }
        }

        /// <summary>
        /// Gets or sets whether parameters from <c>AnimStateMachine.Variables</c> bind automatically.
        /// </summary>
        public bool EnableDynamicParameterBinding
        {
            get => _enableDynamicParameterBinding;
            set => SetField(ref _enableDynamicParameterBinding, value);
        }

        /// <summary>
        /// Gets or sets whether an <see cref="OscQueryServer"/> advertises this receiver over HTTP JSON and mDNS.
        /// </summary>
        public bool EnableOscQuery
        {
            get => _enableOscQuery;
            set => SetField(ref _enableOscQuery, value);
        }

        /// <summary>Gets or sets the advertised OSCQuery service name.</summary>
        public string OscQueryServiceName
        {
            get => _oscQueryServiceName;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? DefaultOscQueryServiceName : value.Trim();
                if (!SetField(ref _oscQueryServiceName, normalized))
                    return;
                if (Server is OscServer server && EnableOscQuery)
                {
                    StartOscQueryServer(server.Port);
                    PublishOscQueryTree(_boundStateMachine);
                }
            }
        }

        /// <summary>
        /// Gets or sets the requested OSCQuery loopback HTTP port. Leave <c>0</c> for an ephemeral port.
        /// </summary>
        public int OscQueryHttpPort
        {
            get => _oscQueryHttpPort;
            set
            {
                int clamped = Math.Clamp(value, 0, 65535);
                if (!SetField(ref _oscQueryHttpPort, clamped))
                    return;
                if (Server is OscServer server && EnableOscQuery)
                {
                    StartOscQueryServer(server.Port);
                    PublishOscQueryTree(_boundStateMachine);
                }
            }
        }

        /// <summary>Gets or sets the avatar identifier advertised at <c>/avatar/change</c>.</summary>
        public string AvatarId
        {
            get => _avatarId;
            set
            {
                string normalized = string.IsNullOrWhiteSpace(value) ? DefaultAvatarId : value.Trim();
                if (!SetField(ref _avatarId, normalized))
                    return;
                if (Server is not null)
                {
                    PublishOscQueryTree(_boundStateMachine);
                    if (_boundStateMachine is not null)
                        TryNotifyAvatarChanged(force: true);
                }
            }
        }

        /// <summary>Gets or sets the avatar display name advertised at <c>/avatar/name</c>.</summary>
        public string AvatarName
        {
            get => _avatarName;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (!SetField(ref _avatarName, normalized))
                    return;
                if (Server is not null)
                    PublishOscQueryTree(_boundStateMachine);
            }
        }

        /// <summary>
        /// Gets or sets the sibling <see cref="HumanoidProceduralEyeGazeComponent"/> coordinated
        /// with this receiver. When <see langword="null"/>, resolves a sibling component on the
        /// same <see cref="SceneNode"/>.
        /// </summary>
        public HumanoidProceduralEyeGazeComponent? ProceduralEyeGaze
        {
            get => _proceduralEyeGaze;
            set
            {
                if (!SetField(ref _proceduralEyeGaze, value))
                    return;
                _cachedSiblingEyeGaze = null;
            }
        }

        /// <summary>
        /// Gets or sets how this receiver switches between live OSC face tracking and procedural
        /// eye gaze and blinking.
        /// </summary>
        public EFaceTrackingControlMode ControlMode
        {
            get => _controlMode;
            set => SetField(ref _controlMode, value);
        }

        /// <summary>
        /// Gets or sets the duration in seconds without incoming live face-tracking packets before
        /// <see cref="EFaceTrackingControlMode.Auto"/> treats the tracking signal as lost.
        /// </summary>
        public float SignalLossTimeoutSeconds
        {
            get => _signalLossTimeoutSeconds;
            set
            {
                float clamped = float.IsFinite(value)
                    ? Math.Clamp(value, 0.25f, 30.0f)
                    : DefaultSignalLossTimeoutSeconds;
                SetField(ref _signalLossTimeoutSeconds, clamped);
            }
        }

        /// <summary>
        /// Gets or sets the smooth fade duration in seconds used to return face-tracking
        /// parameters to their neutral defaults when the tracking signal is lost.
        /// </summary>
        public float NeutralResetDurationSeconds
        {
            get => _neutralResetDurationSeconds;
            set
            {
                float clamped = float.IsFinite(value)
                    ? Math.Clamp(value, 0.0f, 5.0f)
                    : DefaultNeutralResetDurationSeconds;
                SetField(ref _neutralResetDurationSeconds, clamped);
            }
        }

        /// <summary>Gets whether recent live OSC face-tracking packets are arriving.</summary>
        public bool IsTrackingSignalLive => _isTrackingSignalLive;

        /// <summary>Gets whether OSC face tracking currently drives the avatar face.</summary>
        public bool IsFaceTrackingDrivingAvatar => _isFaceTrackingDrivingAvatar;

        /// <summary>Gets whether face-tracking parameters are smoothly returning to neutral.</summary>
        public bool IsRelaxingToNeutral => _isRelaxingToNeutral;

        /// <summary>Gets whether procedural eye gaze and blinking currently owns the avatar eyes.</summary>
        public bool IsProceduralFallbackActive => _isProceduralFallbackActive;

        /// <summary>
        /// Gets elapsed seconds since the most recent live face-tracking packet, or
        /// <c>-1</c> when no tracking packet has arrived.
        /// </summary>
        public float SecondsSinceLastTrackingPacket
        {
            get
            {
                long lastTicks = Volatile.Read(ref _lastTrackingPacketTimestamp);
                if (lastTicks <= 0L)
                    return -1.0f;
                long delta = Stopwatch.GetTimestamp() - lastTicks;
                return delta > 0L ? (float)(delta / (double)Stopwatch.Frequency) : 0.0f;
            }
        }

        /// <summary>Gets the number of external <c>_osc._udp.local.</c> peers currently discovered over mDNS.</summary>
        public int DiscoveredOscQueryPeerCount => _oscQueryServer?.GetDiscoveredPeers().Length ?? 0;

        /// <summary>Gets the number of state-machine parameters currently bound to OSC addresses.</summary>
        public int BoundParameterCount => _boundSlots.Count;

        /// <summary>Gets the active loopback HTTP port of the OSCQuery server, or <c>0</c> when stopped.</summary>
        public int ActiveOscQueryHttpPort => _oscQueryServer?.HttpPort ?? 0;

        /// <summary>Gets whether the OSCQuery HTTP server is currently running.</summary>
        public bool IsOscQueryRunning => _oscQueryServer?.IsRunning ?? false;

        /// <summary>Gets whether the OSCQuery mDNS responder is currently advertising.</summary>
        public bool IsOscQueryMdnsActive => _oscQueryServer?.IsMdnsActive ?? false;

        /// <summary>Gets the most recent OSCQuery error message, if any.</summary>
        public string? LastOscQueryError => _oscQueryServer?.LastError?.Message;

        /// <summary>Gets the total number of parameter OSC messages received by dynamic slots.</summary>
        public long ReceivedParameterPacketCount => Volatile.Read(ref _receivedParameterPacketCount);

        /// <summary>Gets the number of <c>/avatar/change</c> OSC notifications sent to the outbound port.</summary>
        public long AvatarChangeSendCount => Volatile.Read(ref _avatarChangeSendCount);

        /// <summary>
        /// True while this receiver is writing an incoming OSC value into an <see cref="AnimVar"/>.
        /// Outbound OSC senders check this flag to avoid echoing received values back over UDP.
        /// </summary>
        public bool IsApplyingInboundOsc { get; private set; }

        /// <inheritdoc />
        protected override bool AllowAvailablePortFallbackWhenBusy => EnableOscQuery;

        /// <inheritdoc />
        protected override bool ShouldSkipFallbackPort(int candidatePort)
        {
            OscSenderComponent? sender = OutboundSender ?? GetSiblingComponent<OscSenderComponent>();
            return sender is not null && sender.Port == candidatePort;
        }

        /// <summary>Returns the configured or sibling <see cref="AnimStateMachineComponent"/>.</summary>
        public AnimStateMachineComponent? GetAnimStateMachine()
            => AnimStateMachine ?? GetSiblingComponent<AnimStateMachineComponent>();

        /// <summary>
        /// Replaces explicit OSCQuery wire-type overrides (for example, from companion expression
        /// parameter assets where a float Direct Blend Tree parameter is advertised over OSC as bool or int).
        /// </summary>
        public void SetOscQueryParameterTypeOverrides(IReadOnlyDictionary<string, OscQueryValueKind>? overrides)
        {
            _oscQueryParameterTypeOverrides.Clear();
            if (overrides is not null)
            {
                foreach (KeyValuePair<string, OscQueryValueKind> entry in overrides)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value != OscQueryValueKind.None)
                        _oscQueryParameterTypeOverrides[entry.Key] = entry.Value;
                }
            }

            if (Server is not null)
                PublishOscQueryTree(_boundStateMachine);
        }

        /// <summary>
        /// Forces an immediate rebind against the current <see cref="AnimStateMachine"/> schema
        /// and refreshes the OSCQuery tree.
        /// </summary>
        public void RefreshParameterBindings()
        {
            if (Server is null)
                return;

            SyncDynamicSchema(forceRebind: true);
        }

        /// <inheritdoc />
        protected override void OnServerStarted(OscServer server)
        {
            base.OnServerStarted(server);

            if (!_schemaTickRegistered)
            {
                RegisterTick(ETickGroup.Normal, ETickOrder.Input, SyncSchemaTick);
                _schemaTickRegistered = true;
            }

            // Bind parameters before starting OSCQuery so the first mDNS announcement already
            // exposes the populated /avatar tree to already-running clients such as VRCFT.
            SyncDynamicSchema(forceRebind: true);

            if (EnableOscQuery)
                StartOscQueryServer(server.Port, _boundStateMachine);
        }

        /// <inheritdoc />
        protected override void OnServerStopping(OscServer server)
        {
            if (_schemaTickRegistered)
            {
                UnregisterTick(ETickGroup.Normal, ETickOrder.Input, SyncSchemaTick);
                _schemaTickRegistered = false;
            }

            ReleaseFaceTrackingArbitration();
            UnbindDynamicParameters(server);
            StopOscQueryServer();
            _lastNotifiedAvatarId = null;
            _lastNotifiedSchemaVersion = -1;
            _lastNotifiedBoundCount = -1;
            Interlocked.Exchange(ref _pendingOscQueryAvatarChangeNotify, 0);
            Interlocked.Exchange(ref _pendingPeerAvatarChangeNotify, 0);
            base.OnServerStopping(server);
        }

        private void SyncSchemaTick()
        {
            if (Server is null)
                return;

            if (EnableOscQuery && _oscQueryServer is null)
                StartOscQueryServer(Server.Port);
            else if (!EnableOscQuery && _oscQueryServer is not null)
                StopOscQueryServer();

            SyncDynamicSchema(forceRebind: false);

            if (_boundStateMachine is null)
                return;

            UpdateFaceTrackingArbitration();

            // Emit /avatar/change as soon as OutboundSender becomes active, immediately when a
            // new OSCQuery UDP peer is discovered, or after a short cooldown when a client
            // queries HOST_INFO or /avatar over HTTP.
            if (!HasNotifiedCurrentAvatarState())
            {
                TryNotifyAvatarChanged(force: false);
            }
            else if (Interlocked.Exchange(ref _pendingPeerAvatarChangeNotify, 0) != 0)
            {
                TryNotifyAvatarChanged(force: true);
            }
            else if (Volatile.Read(ref _pendingOscQueryAvatarChangeNotify) != 0)
            {
                long elapsed = Stopwatch.GetTimestamp() - _lastAvatarChangeTimestamp;
                if (_lastAvatarChangeTimestamp == 0L || elapsed >= OscQueryNotifyMinIntervalTicks)
                {
                    Interlocked.Exchange(ref _pendingOscQueryAvatarChangeNotify, 0);
                    TryNotifyAvatarChanged(force: true);
                }
            }
        }

        private HumanoidProceduralEyeGazeComponent? ResolveProceduralEyeGaze()
        {
            if (_proceduralEyeGaze is { IsDestroyed: false })
                return _proceduralEyeGaze;

            if (_cachedSiblingEyeGaze is { IsDestroyed: false })
                return _cachedSiblingEyeGaze;

            _cachedSiblingEyeGaze = GetSiblingComponent<HumanoidProceduralEyeGazeComponent>();
            return _cachedSiblingEyeGaze;
        }

        private void UpdateFaceTrackingArbitration()
        {
            AnimStateMachineComponent? animator = GetAnimStateMachine();
            if (animator is null || animator.IsDestroyed || animator.DrivesHumanoidBodyPose)
            {
                _isTrackingSignalLive = false;
                _isFaceTrackingDrivingAvatar = false;
                _isRelaxingToNeutral = false;
                _isProceduralFallbackActive = false;
                return;
            }

            long now = Stopwatch.GetTimestamp();
            float dt = _lastArbitrationTickTimestamp > 0L
                ? Math.Clamp((float)((now - _lastArbitrationTickTimestamp) / (double)Stopwatch.Frequency), 0.0f, 0.10f)
                : 1.0f / 60.0f;
            _lastArbitrationTickTimestamp = now;

            long lastPacketTicks = Volatile.Read(ref _lastTrackingPacketTimestamp);
            long lastExplicitDisableTicks = Volatile.Read(ref _lastExplicitDisableTimestamp);
            bool hasRecentPacket = lastPacketTicks > 0L
                && ((now - lastPacketTicks) / (double)Stopwatch.Frequency) < _signalLossTimeoutSeconds;

            int explicitEye = Volatile.Read(ref _explicitEyeTrackingActive);
            int explicitLip = Volatile.Read(ref _explicitLipTrackingActive);
            int explicitExpr = Volatile.Read(ref _explicitExpressionTrackingActive);
            bool explicitlyAllDisabled = ((explicitEye == 0 && explicitLip == 0 && explicitExpr != 1)
                    || (explicitExpr == 0 && explicitEye != 1 && explicitLip != 1))
                && lastPacketTicks <= lastExplicitDisableTicks;

            _isTrackingSignalLive = hasRecentPacket && !explicitlyAllDisabled;

            bool wantFaceTracking = _controlMode switch
            {
                EFaceTrackingControlMode.ForceFaceTracking => true,
                EFaceTrackingControlMode.ForceProcedural => false,
                _ => _isTrackingSignalLive,
            };

            HumanoidProceduralEyeGazeComponent? eyeGaze = ResolveProceduralEyeGaze();

            if (wantFaceTracking)
            {
                _isRelaxingToNeutral = false;
                _needsNeutralCommit = false;
                _neutralCommitRemainingSeconds = 0.0f;
                _neutralCommitFramesRemaining = 0;

                if (_animatorSuspendedByReceiver)
                {
                    animator.SetEvaluationSuspended(false);
                    _animatorSuspendedByReceiver = false;
                }

                // Restore tracking-active flags if VRCFT previously set them false before live
                // packets resumed or the user forced face tracking on.
                if (explicitEye == 0 && explicitLip == 0)
                    RestoreTrackingStatusDefaultsToActive();

                // Allow procedural eye gaze and blinking to run when VRCFT explicitly disables eye
                // tracking while lip tracking stays active.
                bool suppressEyes = _controlMode == EFaceTrackingControlMode.ForceFaceTracking || explicitEye != 0;
                eyeGaze?.SetExternalSuppression(suppressEyeGaze: suppressEyes, suppressBlinking: suppressEyes);
                _isFaceTrackingDrivingAvatar = true;
                _isProceduralFallbackActive = false;
                return;
            }

            _isFaceTrackingDrivingAvatar = false;

            bool stillRelaxing = StepRelaxTrackingSlotsToNeutral(dt);
            if (stillRelaxing)
            {
                _isRelaxingToNeutral = true;
                _needsNeutralCommit = true;
                _neutralCommitRemainingSeconds = NeutralCommitDurationSeconds;
                _neutralCommitFramesRemaining = NeutralCommitMinFrames;
                if (_animatorSuspendedByReceiver)
                {
                    animator.SetEvaluationSuspended(false);
                    _animatorSuspendedByReceiver = false;
                }

                // Keep procedural eye gaze suppressed while decaying so it does not fight the fade.
                eyeGaze?.SetExternalSuppression(suppressEyeGaze: true, suppressBlinking: true);
                _isProceduralFallbackActive = false;
                return;
            }

            if (_needsNeutralCommit)
            {
                _isRelaxingToNeutral = true;
                if (_animatorSuspendedByReceiver)
                {
                    animator.SetEvaluationSuspended(false);
                    _animatorSuspendedByReceiver = false;
                }

                eyeGaze?.SetExternalSuppression(suppressEyeGaze: true, suppressBlinking: true);
                _neutralCommitRemainingSeconds -= dt;
                _neutralCommitFramesRemaining--;
                if (_neutralCommitRemainingSeconds > 0.0f || _neutralCommitFramesRemaining > 0)
                {
                    _isProceduralFallbackActive = false;
                    return;
                }

                _needsNeutralCommit = false;
            }

            _isRelaxingToNeutral = false;
            if (!_animatorSuspendedByReceiver)
            {
                animator.SetEvaluationSuspended(true);
                _animatorSuspendedByReceiver = true;
            }

            eyeGaze?.SetExternalSuppression(suppressEyeGaze: false, suppressBlinking: false);
            _isProceduralFallbackActive = true;
        }

        private bool StepRelaxTrackingSlotsToNeutral(float dt)
        {
            bool anyRemaining = false;
            bool previousApplying = IsApplyingInboundOsc;
            IsApplyingInboundOsc = true;
            try
            {
                float alpha = _neutralResetDurationSeconds <= 1.0e-4f
                    ? 1.0f
                    : 1.0f - MathF.Exp(-dt * (5.0f / _neutralResetDurationSeconds));

                for (int i = 0; i < _boundSlots.Count; i++)
                {
                    if (_boundSlots[i].StepRelaxToNeutral(alpha))
                        anyRemaining = true;
                }
            }
            finally
            {
                IsApplyingInboundOsc = previousApplying;
            }

            return anyRemaining;
        }

        private void RestoreTrackingStatusDefaultsToActive()
        {
            bool previousApplying = IsApplyingInboundOsc;
            IsApplyingInboundOsc = true;
            try
            {
                for (int i = 0; i < _boundSlots.Count; i++)
                    _boundSlots[i].RestoreStatusDefaultIfActive();
            }
            finally
            {
                IsApplyingInboundOsc = previousApplying;
            }

            Volatile.Write(ref _explicitEyeTrackingActive, -1);
            Volatile.Write(ref _explicitLipTrackingActive, -1);
            Volatile.Write(ref _explicitExpressionTrackingActive, -1);
        }

        private void ReleaseFaceTrackingArbitration()
        {
            if (_animatorSuspendedByReceiver)
            {
                if (GetAnimStateMachine() is { IsDestroyed: false } animator)
                    animator.SetEvaluationSuspended(false);
                _animatorSuspendedByReceiver = false;
            }

            if (ResolveProceduralEyeGaze() is { IsDestroyed: false } eyeGaze)
                eyeGaze.SetExternalSuppression(suppressEyeGaze: false, suppressBlinking: false);

            _isTrackingSignalLive = false;
            _isFaceTrackingDrivingAvatar = false;
            _isRelaxingToNeutral = false;
            _isProceduralFallbackActive = false;
            _needsNeutralCommit = false;
            _neutralCommitRemainingSeconds = 0.0f;
            _neutralCommitFramesRemaining = 0;
            _lastArbitrationTickTimestamp = 0L;
        }

        private static bool IsConfigurationParameterName(string parameterName)
            => LocalConfigurationParameters.Contains(parameterName)
                || parameterName.StartsWith("Smoothing/", StringComparison.Ordinal)
                || parameterName.StartsWith("OSCm/", StringComparison.Ordinal)
                || parameterName.StartsWith("State/", StringComparison.Ordinal);

        private void SyncDynamicSchema(bool forceRebind)
        {
            if (Server is not OscServer server)
                return;

            if (!EnableDynamicParameterBinding)
            {
                if (_boundSlots.Count > 0 || _boundStateMachine is not null)
                {
                    ReleaseFaceTrackingArbitration();
                    UnbindDynamicParameters(server);
                    PublishOscQueryTree(null);
                }
                return;
            }

            AnimStateMachine? stateMachine = GetAnimStateMachine()?.StateMachine;
            if (stateMachine is null)
            {
                if (_boundSlots.Count > 0 || _boundStateMachine is not null)
                {
                    ReleaseFaceTrackingArbitration();
                    UnbindDynamicParameters(server);
                    PublishOscQueryTree(null);
                }
                return;
            }

            string normalizedPrefix = NormalizePrefix(ParameterPrefix);
            int schemaVersion = stateMachine.ParameterSchemaVersion;
            if (!forceRebind
                && ReferenceEquals(stateMachine, _boundStateMachine)
                && schemaVersion == _boundSchemaVersion
                && string.Equals(normalizedPrefix, _boundPrefix, StringComparison.Ordinal))
            {
                return;
            }

            ReleaseFaceTrackingArbitration();
            UnbindDynamicParameters(server);
            BindDynamicParameters(server, stateMachine, normalizedPrefix);
            _boundStateMachine = stateMachine;
            _boundSchemaVersion = schemaVersion;
            _boundPrefix = normalizedPrefix;

            PublishOscQueryTree(stateMachine);
            TryNotifyAvatarChanged(force: true);
        }

        private void BindDynamicParameters(
            OscServer server,
            AnimStateMachine stateMachine,
            string normalizedPrefix)
        {
            Volatile.Write(ref _lastTrackingPacketTimestamp, 0L);
            Volatile.Write(ref _lastExplicitDisableTimestamp, 0L);
            Volatile.Write(ref _explicitEyeTrackingActive, -1);
            Volatile.Write(ref _explicitLipTrackingActive, -1);
            Volatile.Write(ref _explicitExpressionTrackingActive, -1);

            foreach (KeyValuePair<string, AnimVar> entry in stateMachine.Variables)
            {
                string parameterName = entry.Key;
                AnimVar variable = entry.Value;
                if (string.IsNullOrWhiteSpace(parameterName)
                    || parameterName.StartsWith(XREngine.Animation.AnimStateMachine.ScopedInternalParameterPrefix, StringComparison.Ordinal))
                    continue;
                if (variable is not (AnimFloat or AnimBool or AnimInt))
                    continue;

                // Skip static addresses already registered by a legacy subclass to avoid double dispatch.
                if (ShouldRegisterStaticReceiverAddresses && ReceiverAddresses.ContainsKey(parameterName))
                    continue;

                string address = parameterName.StartsWith('/')
                    ? parameterName
                    : $"{normalizedPrefix}{parameterName}";

                if (!_boundAddresses.Add(address))
                    continue;

                var slot = new BoundParameterSlot(this, parameterName, address, variable);
                if (server.TryAddMethodPair(address, slot.ActionPair))
                    _boundSlots.Add(slot);
                else
                    _boundAddresses.Remove(address);
            }

            Debug.Networking(
                $"[OscReceiver] Bound {_boundSlots.Count} dynamic OSC parameters on UDP port {server.Port} (schema v{stateMachine.ParameterSchemaVersion}).");
        }

        private void UnbindDynamicParameters(OscServer server)
        {
            for (int i = 0; i < _boundSlots.Count; i++)
            {
                BoundParameterSlot slot = _boundSlots[i];
                server.RemoveMethodPair(slot.Address, slot.ActionPair);
            }

            _boundSlots.Clear();
            _boundAddresses.Clear();
            _boundStateMachine = null;
            _boundSchemaVersion = -1;
            _boundPrefix = string.Empty;
        }

        private void StartOscQueryServer(int oscPort, AnimStateMachine? initialStateMachine = null)
        {
            StopOscQueryServer();

            var options = new OscQueryServerOptions
            {
                ServiceName = OscQueryServiceName,
                OscPort = oscPort,
                HttpPort = OscQueryHttpPort,
                EnableMdns = true,
            };

            var server = new OscQueryServer(options);
            server.ServerError += OnOscQueryServerError;
            server.RequestHandled += OnOscQueryRequestHandled;
            server.PeerDiscovered += OnOscQueryPeerDiscovered;
            server.UpdateTree(BuildOscQueryTree(initialStateMachine ?? _boundStateMachine));
            try
            {
                server.Start();
                _oscQueryServer = server;
                Debug.Networking(
                    $"[OscQuery] Advertising '{server.ServiceName}' on HTTP 127.0.0.1:{server.HttpPort} -> OSC UDP {server.OscPort} (mDNS: {server.IsMdnsActive}).");
            }
            catch (Exception exception)
            {
                server.ServerError -= OnOscQueryServerError;
                server.RequestHandled -= OnOscQueryRequestHandled;
                server.PeerDiscovered -= OnOscQueryPeerDiscovered;
                server.Dispose();
                Debug.NetworkingWarning($"[OscQuery] Failed to start OSCQuery server: {exception.Message}");
            }
        }

        private void StopOscQueryServer()
        {
            if (_oscQueryServer is not OscQueryServer server)
                return;

            server.ServerError -= OnOscQueryServerError;
            server.RequestHandled -= OnOscQueryRequestHandled;
            server.PeerDiscovered -= OnOscQueryPeerDiscovered;
            server.Dispose();
            _oscQueryServer = null;
        }

        private void OnOscQueryServerError(object? sender, OscServerErrorEventArgs args)
            => Debug.NetworkingWarning($"[OscQuery] {args.Exception.Message}");

        private void OnOscQueryRequestHandled(object? sender, OscQueryRequestEventArgs args)
        {
            string requestPath = args.RequestPath;
            if (requestPath.Equals("HOST_INFO", StringComparison.OrdinalIgnoreCase)
                || requestPath.Equals("/", StringComparison.Ordinal)
                || requestPath.Equals("/avatar", StringComparison.OrdinalIgnoreCase)
                || requestPath.StartsWith("/avatar/", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _pendingOscQueryAvatarChangeNotify, 1);
            }
        }

        private void OnOscQueryPeerDiscovered(object? sender, OscQueryPeerEventArgs args)
        {
            Debug.Networking(
                $"[OscQuery] Discovered OSC peer '{args.Peer.ServiceName}' at {args.Peer.Address}:{args.Peer.Port}.");
            Interlocked.Exchange(ref _pendingPeerAvatarChangeNotify, 1);
        }

        private void PublishOscQueryTree(AnimStateMachine? stateMachine)
        {
            if (_oscQueryServer is not OscQueryServer queryServer)
                return;

            queryServer.UpdateTree(BuildOscQueryTree(stateMachine));
            queryServer.Announce();
        }

        private OscQueryNode BuildOscQueryTree(AnimStateMachine? stateMachine)
        {
            string normalizedPrefix = NormalizePrefix(ParameterPrefix);
            string displayName = !string.IsNullOrWhiteSpace(AvatarName)
                ? AvatarName
                : (!string.IsNullOrWhiteSpace(SceneNode?.Name) ? SceneNode.Name : "XRENGINE Avatar");

            var builder = new OscQueryTreeBuilder();
            builder.AddContainer("/avatar", "Active avatar endpoints");
            builder.AddEndpoint(
                AvatarChangeAddress,
                "s",
                OscQueryAccess.ReadOnly,
                OscQueryValue.FromText(AvatarId),
                "Active avatar identifier");
            builder.AddEndpoint(
                AvatarNameAddress,
                "s",
                OscQueryAccess.ReadOnly,
                OscQueryValue.FromText(displayName),
                "Active avatar name");
            builder.AddEndpoint(
                AvatarFullFaceTrackingAddress,
                "F",
                OscQueryAccess.ReadOnly,
                OscQueryValue.FromBool(false),
                "Native full face tracking flag");
            builder.AddContainer(normalizedPrefix.TrimEnd('/'), "Avatar animator parameters");

            if (stateMachine is not null)
            {
                foreach (KeyValuePair<string, AnimVar> entry in stateMachine.Variables)
                {
                    string parameterName = entry.Key;
                    if (string.IsNullOrWhiteSpace(parameterName)
                        || parameterName.StartsWith(XREngine.Animation.AnimStateMachine.ScopedInternalParameterPrefix, StringComparison.Ordinal))
                        continue;

                    string fullPath = parameterName.StartsWith('/')
                        ? parameterName
                        : $"{normalizedPrefix}{parameterName}";

                    OscQueryValueKind advertisedKind = ResolveOscQueryParameterKind(parameterName, entry.Value);
                    switch (advertisedKind)
                    {
                        case OscQueryValueKind.FloatingPoint:
                            builder.AddEndpoint(fullPath, "f", OscQueryAccess.ReadWrite, OscQueryValue.FromFloat(entry.Value.FloatValue));
                            break;
                        case OscQueryValueKind.WholeNumber:
                            builder.AddEndpoint(fullPath, "i", OscQueryAccess.ReadWrite, OscQueryValue.FromInt(entry.Value.IntValue));
                            break;
                        case OscQueryValueKind.Boolean:
                            bool boolValue = entry.Value.BoolValue;
                            builder.AddEndpoint(fullPath, boolValue ? "T" : "F", OscQueryAccess.ReadWrite, OscQueryValue.FromBool(boolValue));
                            break;
                    }
                }
            }

            return builder.Build();
        }

        private OscQueryValueKind ResolveOscQueryParameterKind(string parameterName, AnimVar variable)
        {
            if (_oscQueryParameterTypeOverrides.TryGetValue(parameterName, out OscQueryValueKind overrideKind))
                return overrideKind;

            if (variable is AnimFloat && StandardOscBoolParameters.Contains(parameterName))
                return OscQueryValueKind.Boolean;

            return variable switch
            {
                AnimFloat => OscQueryValueKind.FloatingPoint,
                AnimInt => OscQueryValueKind.WholeNumber,
                AnimBool => OscQueryValueKind.Boolean,
                _ => OscQueryValueKind.None,
            };
        }

        /// <summary>
        /// Sends the VRChat-compatible <c>/avatar/change</c> OSC string message with
        /// <see cref="AvatarId"/> to the configured outbound OSC port (default UDP 9001) and to
        /// any discovered external <c>_osc._udp.local.</c> peers.
        /// </summary>
        public bool NotifyAvatarChanged()
            => TryNotifyAvatarChanged(force: true);

        private bool HasNotifiedCurrentAvatarState()
            => string.Equals(_lastNotifiedAvatarId, AvatarId, StringComparison.Ordinal)
                && _lastNotifiedSchemaVersion == _boundSchemaVersion
                && _lastNotifiedBoundCount == _boundSlots.Count;

        private bool TryNotifyAvatarChanged(bool force)
        {
            if (!force && HasNotifiedCurrentAvatarState())
                return false;

            OscSenderComponent? sender = OutboundSender ?? GetSiblingComponent<OscSenderComponent>();
            OscQueryPeerEndpoint[] peers = _oscQueryServer?.GetDiscoveredPeers() ?? [];
            bool sentPrimary = false;
            int primaryPort = 0;

            if (sender is not null && sender.Client is not null)
            {
                sender.Send(AvatarChangeAddress, AvatarId);
                sentPrimary = true;
                primaryPort = sender.Port;
            }

            int peerSends = 0;
            for (int i = 0; i < peers.Length; i++)
            {
                OscQueryPeerEndpoint peer = peers[i];
                if (peer.Port <= 0)
                    continue;
                if (sentPrimary && peer.Port == primaryPort && System.Net.IPAddress.IsLoopback(peer.Address))
                    continue;

                try
                {
                    using var peerClient = new OscClient(peer.Address.ToString(), peer.Port);
                    peerClient.Send(AvatarChangeAddress, AvatarId);
                    peerSends++;
                }
                catch (Exception ex)
                {
                    Debug.NetworkingWarning(
                        $"[OscReceiver] Failed sending {AvatarChangeAddress} to discovered peer '{peer.ServiceName}' ({peer.Address}:{peer.Port}): {ex.Message}");
                }
            }

            if (!sentPrimary && peerSends == 0)
                return false;

            _lastNotifiedAvatarId = AvatarId;
            _lastNotifiedSchemaVersion = _boundSchemaVersion;
            _lastNotifiedBoundCount = _boundSlots.Count;
            _lastAvatarChangeTimestamp = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _avatarChangeSendCount);
            Interlocked.Exchange(ref _pendingOscQueryAvatarChangeNotify, 0);
            Interlocked.Exchange(ref _pendingPeerAvatarChangeNotify, 0);

            Debug.Networking(
                $"[OscReceiver] Sent {AvatarChangeAddress} '{AvatarId}' (primaryPort={(sentPrimary ? primaryPort : 0)}, oscQueryPeers={peerSends}, boundParams={_boundSlots.Count}).");
            return true;
        }

        private static string NormalizePrefix(string? prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return DefaultAvatarParameterPrefix;

            string trimmed = prefix.Trim();
            if (!trimmed.StartsWith('/'))
                trimmed = "/" + trimmed;
            if (!trimmed.EndsWith('/'))
                trimmed += "/";
            return trimmed;
        }

        private sealed class BoundParameterSlot
        {
            private const int KindNone = 0;
            private const int KindFloat = 1;
            private const int KindInt = 2;
            private const int KindBool = 3;

            private const int StatusFlagNone = 0;
            private const int StatusFlagEye = 1;
            private const int StatusFlagLip = 2;
            private const int StatusFlagExpression = 3;

            private readonly AnimStateMachineOscReceiverComponent _owner;
            private readonly AnimVar _variable;
            private readonly int _statusFlagKind;
            private readonly bool _isConfigurationParameter;
            private readonly float _initialFloatValue;
            private readonly int _initialIntValue;
            private readonly bool _initialBoolValue;

            private int _pendingFloatBits;
            private int _pendingInt;
            private int _pendingBool;
            private int _pendingKind;
            private int _hasReceivedInboundPacket;

            public BoundParameterSlot(
                AnimStateMachineOscReceiverComponent owner,
                string parameterName,
                string address,
                AnimVar variable)
            {
                _owner = owner;
                Address = address;
                _variable = variable;
                _statusFlagKind = parameterName switch
                {
                    "EyeTrackingActive" => StatusFlagEye,
                    "LipTrackingActive" => StatusFlagLip,
                    "ExpressionTrackingActive" => StatusFlagExpression,
                    _ => StatusFlagNone,
                };
                _isConfigurationParameter = _statusFlagKind == StatusFlagNone
                    && IsConfigurationParameterName(parameterName);
                _initialFloatValue = variable.FloatValue;
                _initialIntValue = variable.IntValue;
                _initialBoolValue = variable.BoolValue;
                ActionPair = new OscActionPair(ReadFromPacket, ApplyOnMainThread);
            }

            public string Address { get; }
            public OscActionPair ActionPair { get; }

            public bool StepRelaxToNeutral(float alpha)
            {
                if (_statusFlagKind != StatusFlagNone || _isConfigurationParameter)
                    return false;

                if (Volatile.Read(ref _hasReceivedInboundPacket) == 0)
                    return false;

                switch (_variable)
                {
                    case AnimFloat f:
                    {
                        float current = f.Value;
                        float target = _initialFloatValue;
                        float delta = target - current;
                        if (!float.IsFinite(current) || MathF.Abs(delta) <= NeutralFloatSnapEpsilon || alpha >= 0.999f)
                        {
                            if (current != target)
                                f.Value = target;
                            return false;
                        }

                        float next = current + delta * alpha;
                        if (MathF.Abs(target - next) <= NeutralFloatSnapEpsilon)
                        {
                            f.Value = target;
                            return false;
                        }

                        f.Value = next;
                        return true;
                    }

                    case AnimBool b:
                        if (b.Value != _initialBoolValue)
                            b.Value = _initialBoolValue;
                        return false;

                    case AnimInt i:
                        if (i.Value != _initialIntValue)
                            i.Value = _initialIntValue;
                        return false;

                    default:
                        return false;
                }
            }

            public void RestoreStatusDefaultIfActive()
            {
                if (_statusFlagKind == StatusFlagNone)
                    return;

                switch (_variable)
                {
                    case AnimFloat f when _initialFloatValue > 0.5f && f.Value < 0.5f:
                        f.Value = _initialFloatValue;
                        break;
                    case AnimBool b when _initialBoolValue && !b.Value:
                        b.Value = true;
                        break;
                    case AnimInt i when _initialIntValue != 0 && i.Value == 0:
                        i.Value = _initialIntValue;
                        break;
                }
            }

            private void ReadFromPacket(OscMessageValues values)
            {
                if (values.ElementCount <= 0)
                    return;

                TypeTag tag = values.GetTypeTag(0);
                bool boolEquivalent;
                switch (tag)
                {
                    case TypeTag.Float32:
                    {
                        float f = values.ReadFloatElementUnchecked(0);
                        boolEquivalent = f > 0.5f;
                        Volatile.Write(ref _pendingFloatBits, BitConverter.SingleToInt32Bits(f));
                        Volatile.Write(ref _pendingKind, KindFloat);
                        break;
                    }
                    case TypeTag.Float64:
                    {
                        float f = (float)values.ReadFloat64ElementUnchecked(0);
                        boolEquivalent = f > 0.5f;
                        Volatile.Write(ref _pendingFloatBits, BitConverter.SingleToInt32Bits(f));
                        Volatile.Write(ref _pendingKind, KindFloat);
                        break;
                    }
                    case TypeTag.Int32:
                    {
                        int i = values.ReadIntElementUnchecked(0);
                        boolEquivalent = i != 0;
                        Volatile.Write(ref _pendingInt, i);
                        Volatile.Write(ref _pendingKind, KindInt);
                        break;
                    }
                    case TypeTag.Int64:
                    {
                        int i = (int)values.ReadInt64ElementUnchecked(0);
                        boolEquivalent = i != 0;
                        Volatile.Write(ref _pendingInt, i);
                        Volatile.Write(ref _pendingKind, KindInt);
                        break;
                    }
                    case TypeTag.True:
                        boolEquivalent = true;
                        Volatile.Write(ref _pendingBool, 1);
                        Volatile.Write(ref _pendingKind, KindBool);
                        break;
                    case TypeTag.False:
                        boolEquivalent = false;
                        Volatile.Write(ref _pendingBool, 0);
                        Volatile.Write(ref _pendingKind, KindBool);
                        break;
                    default:
                        return;
                }

                Volatile.Write(ref _hasReceivedInboundPacket, 1);
                Interlocked.Increment(ref _owner._receivedParameterPacketCount);

                long now = Stopwatch.GetTimestamp();
                if (_statusFlagKind != StatusFlagNone)
                {
                    int flagValue = boolEquivalent ? 1 : 0;
                    switch (_statusFlagKind)
                    {
                        case StatusFlagEye:
                            Volatile.Write(ref _owner._explicitEyeTrackingActive, flagValue);
                            break;
                        case StatusFlagLip:
                            Volatile.Write(ref _owner._explicitLipTrackingActive, flagValue);
                            break;
                        case StatusFlagExpression:
                            Volatile.Write(ref _owner._explicitExpressionTrackingActive, flagValue);
                            break;
                    }

                    if (boolEquivalent)
                        Volatile.Write(ref _owner._lastTrackingPacketTimestamp, now);
                    else
                        Volatile.Write(ref _owner._lastExplicitDisableTimestamp, now);
                }
                else if (!_isConfigurationParameter)
                {
                    Volatile.Write(ref _owner._lastTrackingPacketTimestamp, now);
                }
            }

            private void ApplyOnMainThread()
            {
                int kind = Interlocked.Exchange(ref _pendingKind, KindNone);
                if (kind == KindNone)
                    return;

                // While procedural mode is forced, ignore incoming face-pose values so the face
                // stays neutral while still updating signal diagnostics above.
                if (_owner._controlMode == EFaceTrackingControlMode.ForceProcedural
                    && _statusFlagKind == StatusFlagNone
                    && !_isConfigurationParameter)
                {
                    return;
                }

                bool previous = _owner.IsApplyingInboundOsc;
                _owner.IsApplyingInboundOsc = true;
                try
                {
                    switch (_variable)
                    {
                        case AnimFloat f:
                            f.Value = kind switch
                            {
                                KindFloat => BitConverter.Int32BitsToSingle(Volatile.Read(ref _pendingFloatBits)),
                                KindInt => Volatile.Read(ref _pendingInt),
                                KindBool => Volatile.Read(ref _pendingBool) != 0 ? 1.0f : 0.0f,
                                _ => f.Value,
                            };
                            break;

                        case AnimBool b:
                            b.Value = kind switch
                            {
                                KindBool => Volatile.Read(ref _pendingBool) != 0,
                                KindInt => Volatile.Read(ref _pendingInt) != 0,
                                KindFloat => BitConverter.Int32BitsToSingle(Volatile.Read(ref _pendingFloatBits)) > 0.5f,
                                _ => b.Value,
                            };
                            break;

                        case AnimInt i:
                            i.Value = kind switch
                            {
                                KindInt => Volatile.Read(ref _pendingInt),
                                KindFloat => (int)BitConverter.Int32BitsToSingle(Volatile.Read(ref _pendingFloatBits)),
                                KindBool => Volatile.Read(ref _pendingBool) != 0 ? 1 : 0,
                                _ => i.Value,
                            };
                            break;
                    }
                }
                finally
                {
                    _owner.IsApplyingInboundOsc = previous;
                }
            }
        }
    }
}
