using System.Net.Sockets;
using System.Net;
using System.Numerics;
using System.Text;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Scene.Transforms;
using Extensions;

namespace XREngine.Components;

public class FaceMotion3DCaptureComponent : XRComponent
{
    private const string Command = "FACEMOTION3D_OtherStreaming";
    private const string TrackingStatusIdent = "trackingStatus";
    private string _phoneIP = "192.168.0.172";
    private int _phonePort = 49983;
    private int _udpListenPort = 49983;
    private int _tcpListenPort = 49986;
    private bool _tcpMode = false;
    private bool _isStreaming = false;
    private HumanoidComponent? _humanoid;

    public bool IsStreaming
    {
        get => _isStreaming;
        private set => SetField(ref _isStreaming, value);
    }

    public bool TCPMode
    {
        get => _tcpMode;
        set => SetField(ref _tcpMode, value);
    }

    public string PhoneIP
    {
        get => _phoneIP;
        set => SetField(ref _phoneIP, value);
    }

    public int PhonePort
    {
        get => _phonePort;
        set => SetField(ref _phonePort, value);
    }

    public int UDPListenPort
    {
        get => _udpListenPort;
        set => SetField(ref _udpListenPort, value);
    }

    public int TCPListenPort
    {
        get => _tcpListenPort;
        set => SetField(ref _tcpListenPort, value);
    }

    public HumanoidComponent? Humanoid
    {
        get => _humanoid;
        set => SetField(ref _humanoid, value);
    }

    private float _headDepthScale = 0.01f;
    public float HeadDepthScale
    {
        get => _headDepthScale;
        set => SetField(ref _headDepthScale, value);
    }

    private float _headLeftRightScale = 0.1f;
    public float HeadLeftRightScale
    {
        get => _headLeftRightScale;
        set => SetField(ref _headLeftRightScale, value);
    }

    private float _headUpDownScale = 0.1f;
    public float HeadUpDownScale
    {
        get => _headUpDownScale;
        set => SetField(ref _headUpDownScale, value);
    }

    private float _headNodScale = 0.9f;
    public float HeadNodScale
    {
        get => _headNodScale;
        set => SetField(ref _headNodScale, value);
    }

    private float _headTiltScale = 0.9f;
    public float HeadTiltScale
    {
        get => _headTiltScale;
        set => SetField(ref _headTiltScale, value);
    }

    private float _headLookScale = 0.9f;
    public float HeadLookScale
    {
        get => _headLookScale;
        set => SetField(ref _headLookScale, value);
    }

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        StartStreaming();
    }

    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
        StopStreaming();
    }

    public void StartStreaming()
    {
        if (_tcpMode)
            StartTcpStreaming();
        else
            StartUdpStreaming();
    }

    public void StopStreaming()
    {
        IsStreaming = false;
        _udpListener?.Close();
    }

    private UdpClient? _udpListener;

    /// <summary>
    /// UDP streaming mode:
    /// Sends "FACEMOTION3D_OtherStreaming" to the iOS device on port 49993,
    /// then listens for incoming UDP data (received at port 49983 as per sample).
    /// </summary>
    private void StartUdpStreaming()
    {
        // Send the command via UDP to the iOS device
        using (UdpClient sender = new())
        {
            byte[] commandBytes = Encoding.UTF8.GetBytes(Command);
            sender.Send(commandBytes, commandBytes.Length, PhoneIP, PhonePort);
        }

        _udpListener = new(UDPListenPort);
        _udpListener.Client.ReceiveTimeout = 50;

        IsStreaming = true;

        // Start an asynchronous receive
        IPEndPoint? remoteEP = new(IPAddress.Any, 0);
        _udpListener.BeginReceive(OnUdpDataReceived, (remoteEP, _udpListener));

        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, ApplyData);
    }

    private Quaternion _leftEyeRotation;
    private Quaternion _rightEyeRotation;
    private Quaternion _headRotation;
    private Vector3 _headPosition;
    private bool _rightEyeInvalidated;
    private bool _leftEyeInvalidated;
    private bool _headInvalidated;

    private void ApplyData()
    {
        if (_udpListener is null || !IsStreaming)
        {
            UnregisterTick(ETickGroup.Normal, ETickOrder.Input, ApplyData);
            return;
        }

        if (_rightEyeInvalidated)
        {
            _rightEyeInvalidated = false;
            Humanoid?.Right.Eye?.Node?.GetTransformAs<Transform>(true)?.SetWorldRotation(_rightEyeRotation, true);
        }
        if (_leftEyeInvalidated)
        {
            _leftEyeInvalidated = false;
            Humanoid?.Left.Eye?.Node?.GetTransformAs<Transform>(true)?.SetWorldRotation(_leftEyeRotation, true);
        }
        if (_headInvalidated)
        {
            _headInvalidated = false;
            ApplyHeadTransform();
        }
    }

    private void ApplyHeadTransform()
    {
        if (Humanoid is null)
            return;

        var headTfm = Humanoid.Head?.Node?.GetTransformAs<Transform>(true);
        if (headTfm is null)
            return;

        Vector3 headWorldTranslation = _headPosition;
        var neckWorldPose = Humanoid.Neck.WorldBindPose;
        headWorldTranslation += neckWorldPose.Translation;

        headTfm.SetWorldRotation(_headRotation, true);
        Humanoid.HeadTarget = (null, Matrix4x4.CreateTranslation(headWorldTranslation));

        //Humanoid.Hips.Node?.Transform?.DeriveWorldMatrix(Matrix4x4.CreateTranslation(headWorldTranslation.X, Humanoid.Hips.WorldBindPose.Translation.Y, headWorldTranslation.Z), false);
    }

    private void OnUdpDataReceived(IAsyncResult result)
    {
        if (!IsStreaming)
            return;

        try
        {
            var state = ((IPEndPoint?, UdpClient))result.AsyncState!;
            IPEndPoint? remoteEP = state.Item1;
            var client = state.Item2;

            // Complete the receive
            byte[] buffer = client.EndReceive(result, ref remoteEP);

            // Process the received data
            if (buffer.Length > 0)
            {
                string data = Encoding.UTF8.GetString(buffer);
                ParseFrame(data);
            }

            // Start the next receive if still streaming
            if (IsStreaming)
            {
                remoteEP = new IPEndPoint(IPAddress.Any, 0);
                client.BeginReceive(OnUdpDataReceived, (remoteEP, client));
            }
        }
        catch (ObjectDisposedException)
        {
            // Socket was closed, just return
            return;
        }
        catch (SocketException ex)
        {
            // Only log if it's not a timeout
            if (ex.SocketErrorCode != SocketError.TimedOut)
                Debug.Out("Error receiving UDP data: " + ex.Message);

            // Try to restart the receive if still streaming
            if (IsStreaming && _udpListener != null)
            {
                EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                _udpListener.BeginReceive(OnUdpDataReceived, (remoteEP, _udpListener));
            }
        }
        catch (Exception ex)
        {
            Debug.Out("Unexpected error in UDP receive: " + ex.Message);
        }
    }

    /// <summary>
    /// TCP streaming mode:
    /// Sends "FACEMOTION3D_OtherStreaming|protocol=tcp" via UDP to the iOS device on port 49993,
    /// then starts a TCP server on port 49986 to receive streaming data.
    /// Each frame is delimited with "___FACEMOTION3D".
    /// </summary>
    private void StartTcpStreaming()
    {
        string udpCommand = $"{Command}|protocol=tcp";
        try
        {
            // Send the UDP command to start TCP streaming on the iOS device
            using (UdpClient sender = new())
            {
                byte[] commandBytes = Encoding.UTF8.GetBytes(udpCommand);
                sender.Send(commandBytes, commandBytes.Length, PhoneIP, PhonePort);
                Console.WriteLine($"[TCP] UDP command sent: {udpCommand}");
            }

            // Start the TCP listener
            TcpListener tcpListener = new(IPAddress.Any, TCPListenPort);
            tcpListener.Start();
            Debug.Out($"[TCP] Listening for TCP clients on port {TCPListenPort}");

            IsStreaming = true;
            while (IsStreaming)
            {
                TcpClient client = tcpListener.AcceptTcpClient();
                Debug.Out($"[TCP] Client connected: {client.Client.RemoteEndPoint}");

                // Process the TCP connection in a separate thread (or inline, if you prefer)
                Thread clientThread = new(() => HandleTcpClient(client));
                clientThread.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Error in TCP streaming: ");
            IsStreaming = false;
        }
    }

    /// <summary>
    /// Handles the connected TCP client.
    /// Reads data from the stream, and extracts complete frames using the delimiter "___FACEMOTION3D".
    /// </summary>
    /// <param name="client">The connected TcpClient.</param>
    private void HandleTcpClient(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[10000]; // buffer size as per sample
            StringBuilder dataBuilder = new();
            string delimiter = "___FACEMOTION3D";

            while (client.Connected && IsStreaming)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    // Connection closed
                    break;
                }

                // Append received data
                dataBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                string data = dataBuilder.ToString();

                // Look for complete frames delimited by "___FACEMOTION3D"
                int delimiterIndex;
                while ((delimiterIndex = data.IndexOf(delimiter)) >= 0)
                {
                    string completeFrame = data[..delimiterIndex];
                    ParseFrame(completeFrame);
                    // Remove the processed frame from the data buffer
                    data = data[(delimiterIndex + delimiter.Length)..];
                }
                // Update the builder with any incomplete data left over
                dataBuilder.Clear();
                dataBuilder.Append(data);
            }
            Debug.Out("[TCP] Client disconnected");
            client.Close();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Error handling TCP client: ");
        }
    }

    private void ParseFrame(string completeFrame)
        => completeFrame.Split('|', StringSplitOptions.RemoveEmptyEntries).ForEach(ParseData);

    private void ParseData(string frame)
    {
        if (frame.Contains('#')) //bone
        {
            string[] parts = frame.Split('#');
            if (parts.Length != 2)
                return;

            string boneName = parts[0];
            switch (boneName)
            {
                case "=head":
                    ParseHead(parts[1]);
                    break;
                case "rightEye":
                    ParseEye(parts[1], true);
                    break;
                case "leftEye":
                    ParseEye(parts[1], false);
                    break;
            }
        }
        else if (frame.Contains('-')) //blendshape
        {
            string[] parts = frame.Split('-');
            if (parts.Length != 2)
                return;

            ParseBlendshape(parts, false);
        }
        else if (frame.Contains('&')) //blendshape
        {
            string[] parts = frame.Split('&');
            if (parts.Length != 2)
                return;

            ParseBlendshape(parts, true);
        }
        else
        {
            Debug.Out("Unknown frame data: " + frame);
        }
    }

    private bool _isTracking;
    public bool IsTracking
    {
        get => _isTracking;
        private set => SetField(ref _isTracking, value);
    }

    private void ParseBlendshape(string[] parts, bool ampersand)
    {
        string blendShapeName = parts[0];
        if (!ampersand && string.Equals(blendShapeName, TrackingStatusIdent))
        {
            if (int.TryParse(parts[1], out int status))
                IsTracking = status != 0;
            return;
        }

        if (float.TryParse(parts[1], out float percentage))
        {
            //Debug.Out($"Setting blendshape {blendShapeName} to {percentage} (&: {ampersand})");
            Humanoid?.SetBlendshapeValue(blendShapeName, percentage, false);
        }
        else
            Debug.Out("Failed to parse blendshape percentage: " + parts[1]);
    }

    private float _eyeLeftRightScale = 1.0f;
    public float EyeLeftRightScale
    {
        get => _eyeLeftRightScale;
        set => SetField(ref _eyeLeftRightScale, value);
    }

    private float _eyeUpDownScale = 1.0f;
    public float EyeUpDownScale
    {
        get => _eyeUpDownScale;
        set => SetField(ref _eyeUpDownScale, value);
    }

    private float _eyeRotationScale = 0.1f;
    public float EyeRotationScale
    {
        get => _eyeRotationScale;
        set => SetField(ref _eyeRotationScale, value);
    }

    private void ParseEye(string data, bool rightEye)
    {
        //euler xyz

        string[] values = data.Split(',');
        if (values.Length != 3)
            return;

        if (!float.TryParse(values[0], out float x) ||
            !float.TryParse(values[1], out float y) ||
            !float.TryParse(values[2], out float z))
            return;

        var rot = Quaternion.CreateFromYawPitchRoll(
            float.DegreesToRadians(y * EyeLeftRightScale),
            float.DegreesToRadians(x * EyeUpDownScale),
            float.DegreesToRadians(z * EyeRotationScale));

        if (rightEye)
        {
            _rightEyeRotation = rot;
            _rightEyeInvalidated = true;
        }
        else
        {
            _leftEyeRotation = rot;
            _leftEyeInvalidated = true;
        }
    }

    private void ParseHead(string data)
    {
        //euler xyz, position xyz

        string[] values = data.Split(',');
        if (values.Length != 6)
            return;

        if (!float.TryParse(values[0], out float x) ||
            !float.TryParse(values[1], out float y) ||
            !float.TryParse(values[2], out float z) ||
            !float.TryParse(values[3], out float px) ||
            !float.TryParse(values[4], out float py) ||
            !float.TryParse(values[5], out float pz))
            return;

        _headInvalidated = true;
        _headRotation = Quaternion.CreateFromYawPitchRoll(
            float.DegreesToRadians(y * HeadLookScale), 
            float.DegreesToRadians(x * HeadNodScale),
            float.DegreesToRadians(z * HeadTiltScale));
        _headPosition = new Vector3(
            px * HeadLeftRightScale,
            py * HeadUpDownScale,
            pz * HeadDepthScale);
    }
}