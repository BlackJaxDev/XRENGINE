using BlobHandles;
using OscCore;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Components.Animation;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

/// <summary>
/// Receives and processes VMC motion capture information.
/// </summary>
public class VMCCaptureComponent : VMCComponent
{
    public OscServer? Server { get; private set; } = null;
    public HumanoidComponent? Humanoid { get; set; }
    public CameraComponent? Camera { get; set; }
    public DirectionalLightComponent[]? Lights { get; set; }

    private const string CMD_Available = "/VMC/Ext/OK"; //int loaded, 2.5:int calib state, int calib mode, 2.7:int tracking status
    //loaded: 0=not loaded, 1=loaded
    //calib state: 0=uncalib, 1=waiting, 2=calibrating, 3=calibrated
    //calib mode: 0=normal, 1=mr normal, 2=mr floor fix
    //tracking status: 0=bad, 1=ok

    private const string CMD_RelativeTime = "/VMC/Ext/T"; //float time
    //Current relative time of the sender. Mainly used to check if communication is possible.

    private const string CMD_RootTransform = "/VMC/Ext/Root/Pos"; //string name, float x, float y, float z, float qx, float qy, float qz, float qw, 2.1:(float sx, float sy, float sz, float ox, float oy, float oz)
    //Model's absolute position, rotation, MR scale, and MR offset. The root bone is used for this / bone name should be 'root'.
    //From v2.1, the position and size of the avatar can be adjusted to the actual body size

    private const string CMD_BoneTransform = "/VMC/Ext/Bone/Pos"; //string name, float x, float y, float z, float qx, float qy, float qz, float qw
    //Sets a bone's position and rotation

    private const string CMD_KeyboardInput = "/VMC/Ext/Key"; //int active, string key name, int key code
    //active: 1=pressed, 0=released

    private const string CMD_ReceiveEnable = "/VMC/Ext/Rcv"; //2.4:int enable, int port (0-65535), 2.7:string ip address
    //enable: 1=enable, 0=disable

    private const string CMD_LocalVRMInfo = "/VMC/Ext/VRM"; //2.4:string path, string title, 2.7:string hash
    //path: local file path to the VRM file
    private const string CMD_RemoteVRMInfo = "/VMC/Ext/Remote"; //string service, string json
    //examples:
    // /VMC/Ext/Remote vroidhub {"characterModelId":"123456789456"}
    // /VMC/Ext/Remote dmmvrconnect {"user_id":"123456789456", "avatar_id":"123456789456"}

    private const string CMD_Option = "/VMC/Ext/Opt"; //string option
    //General-purpose setting character string (recipient arbitrary definition)

    private const string CMD_BackgroundColor = "/VMC/Ext/Setting/Color"; //float r, float g, float b, float a
    //Sets the background color of the application

    private const string CMD_WindowAttribute = "/VMC/Ext/Setting/Win"; //int isTopMost, int isTransparent, int windowClickThrough, int hideBorder
    //1=true, 0=false

    private const string CMD_LoadedSettingPath = "/VMC/Ext/Config"; //string path
    //VMC setting profile path
    
    private const string CMD_EyeTrackingTargetPosition = "/VMC/Ext/Set/Eye"; //int enable, float x, float y, float z
    //enable: 1=enable, 0=disable
    //2.3-2.7: absolute position
    //2.8: head-relative position

    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        Server = OscServer.GetOrCreate(VMC_Port);
        Server.TryAddMethod(CMD_BoneTransform, ParseBoneTransform);
        Server.TryAddMethod(CMD_RootTransform, ParseRootTransform);
        Server.TryAddMethod(CMD_EyeTrackingTargetPosition, ParseEyeTrackingPosition);
        Server.TryAddMethod(CMD_LightTransform, ParseLightTransform);
        Server.TryAddMethod(CMD_BlendshapeValue, ParseBlendshapeValue);
        Server.TryAddMethod(CMD_BlendshapeApply, _ => ApplyBlendShapes());
        Server.TryAddMethod(CMD_CameraTransform, ParseCameraTransform);
        Server.TryAddMethod(CMD_MidiCCButton, ParseMidiCCButton);
        Server.TryAddMethod(CMD_MidiCC, ParseMidiCC);
        Server.TryAddMethod(CMD_MidiNote, ParseMidiNote);
        Server.TryAddMethod(CMD_TrackerPosLocal, ParseTrackerPosLocal);
        Server.TryAddMethod(CMD_ControllerPosLocal, ParseControllerPosLocal);
        Server.TryAddMethod(CMD_HMDPosLocal, ParseHMDPosLocal);
        Server.TryAddMethod(CMD_TrackerPos, ParseTrackerPos);
        Server.TryAddMethod(CMD_ControllerPos, ParseControllerPos);
        Server.TryAddMethod(CMD_HMDPos, ParseHMDPos);
        Server.TryAddMethod(CMD_Available, ParseAvailable);
        Server.TryAddMethod(CMD_RelativeTime, ParseRelativeTime);
        //Server.AddMonitorCallback(PropogateCommand);
        RegisterTick(ETickGroup.Normal, ETickOrder.Input, Update);
    }

    private void PrintCommand(BlobString address, OscMessageValues values)
    {
        Debug.Out($"Received command: {address}");
    }

    private void ParseRelativeTime(OscMessageValues values)
    {
        if (values.ElementCount != 1)
            return;

        //Debug.Out($"Relative time: {values.ReadFloatElement(0)}");
    }

    private void ParseAvailable(OscMessageValues values)
    {
        if (values.ElementCount == 0)
            return;

        bool loaded = values.ReadIntElement(0) != 0;
        int? calibState = null;
        int? calibMode = null;
        int? trackingStatus = null;

        if (values.ElementCount > 1)
        {
            calibState = values.ReadIntElement(1);
            calibMode = values.ReadIntElement(2);
        }

        if (values.ElementCount > 3)
            trackingStatus = values.ReadIntElement(3);

        ApplyAvailable(loaded, calibState, calibMode, trackingStatus);
    }

    private void ApplyAvailable(bool loaded, int? calibState, int? calibMode, int? trackingStatus)
    {
        //Debug.Out($"VMC is {(loaded ? "loaded" : "not loaded")}, calib state: {calibState}, calib mode: {calibMode}, tracking status: {trackingStatus}");
    }

    private void ParseEyeTrackingPosition(OscMessageValues values)
    {
        if (values.ElementCount != 4)
            return;

        ApplyEyeTrackingPosition(
            values.ReadIntElement(0) != 0,
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)));
    }

    private void ApplyEyeTrackingPosition(bool active, Vector3 target)
    {
        if (Humanoid is null)
            return;

        if (active)
            Humanoid.SetEyesLookat(target);
    }

    private void ParseRootTransform(OscMessageValues values)
    {
        if (values.ElementCount < 8)
            return;

        ApplyRootPosition(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)));

        if (values.ElementCount < 14)
            return;

        //From v2.1, the position and size of the avatar can be adjusted to the actual body size
    }

    private void ParseBoneTransform(OscMessageValues values)
    {
        if (values.ElementCount != 8)
            return;

        ApplyBonePosition(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)));
    }

    private void Update()
    {
        Server?.Update();
    }

    private void ParseLightTransform(OscMessageValues values)
    {
        ApplyLightTransform(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            new ColorF4(
                (float)values.ReadFloatElement(8),
                (float)values.ReadFloatElement(9),
                (float)values.ReadFloatElement(10),
                (float)values.ReadFloatElement(11)));
    }

    private void ParseBlendshapeValue(OscMessageValues values)
    {
        _blendshapeQueue.Enqueue((values.ReadStringElement(0), (float)values.ReadFloatElement(1)));
    }

    private void ParseCameraTransform(OscMessageValues values)
    {
        ApplyCameraTransform(
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            (float)values.ReadFloatElement(8));
    }

    private void ParseMidiCCButton(OscMessageValues values)
    {
        ApplyMidiCCButton(
            values.ReadIntElement(0),
            values.ReadIntElement(1) != 0);
    }

    private void ParseMidiCC(OscMessageValues values)
    {
        ApplyMidiCCRadial(
            values.ReadIntElement(0),
            (float)values.ReadFloatElement(1));
    }

    private void ParseMidiNote(OscMessageValues values)
    {
        ApplyMidiNote(
            values.ReadIntElement(0) != 0,
            values.ReadIntElement(1),
            values.ReadIntElement(2),
            (float)values.ReadFloatElement(3));
    }

    private void ParseTrackerPosLocal(OscMessageValues values)
    {
        ApplyTrackerPos(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            true);
    }

    private void ParseControllerPosLocal(OscMessageValues values)
    {
        ApplyControllerPos(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            true);
    }

    private void ParseHMDPosLocal(OscMessageValues values)
    {
        ApplyHMDPos(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            true);
    }

    private void ParseTrackerPos(OscMessageValues values)
    {
        ApplyTrackerPos(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            false);
    }

    private void ParseControllerPos(OscMessageValues values)
    {
        ApplyControllerPos(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            false);
    }

    private void ParseHMDPos(OscMessageValues values)
    {
        ApplyHMDPos(
            values.ReadStringElement(0),
            new Vector3(
                (float)values.ReadFloatElement(1),
                (float)values.ReadFloatElement(2),
                (float)values.ReadFloatElement(3)),
            new Quaternion(
                (float)values.ReadFloatElement(4),
                (float)values.ReadFloatElement(5),
                (float)values.ReadFloatElement(6),
                (float)values.ReadFloatElement(7)),
            false);
    }

    public delegate void DelDeviceTransformRecieved(string serial, Vector3 position, Quaternion rotation, bool local);

    public event DelDeviceTransformRecieved? TrackerTransformRecieved;
    public event DelDeviceTransformRecieved? ControllerTransformRecieved;
    public event DelDeviceTransformRecieved? HMDTransformRecieved;

    public delegate void DelMidiNoteRecieved(bool active, int channel, int note, float velocity);
    public event DelMidiNoteRecieved? MidiNoteRecieved;

    public delegate void DelMidiCCRadialRecieved(int knob, float value);
    public event DelMidiCCRadialRecieved? MidiCCRadialRecieved;

    public delegate void DelMidiCCButtonRecieved(int button, bool active);
    public event DelMidiCCButtonRecieved? MidiCCButtonRecieved;

    private void ApplyTrackerPos(string serial, Vector3 position, Quaternion rotation, bool local)
        => TrackerTransformRecieved?.Invoke(serial, position, rotation, local);
    private void ApplyControllerPos(string serial, Vector3 position, Quaternion rotation, bool local)
        => ControllerTransformRecieved?.Invoke(serial, position, rotation, local);
    private void ApplyHMDPos(string serial, Vector3 position, Quaternion rotation, bool local)
        => HMDTransformRecieved?.Invoke(serial, position, rotation, local);

    private void ApplyMidiNote(bool active, int channel, int note, float velocity)
        => MidiNoteRecieved?.Invoke(active, channel, note, velocity);
    private void ApplyMidiCCRadial(int knob, float value)
        => MidiCCRadialRecieved?.Invoke(knob, value);
    private void ApplyMidiCCButton(int button, bool active)
        => MidiCCButtonRecieved?.Invoke(button, active);

    protected internal override void OnComponentDeactivated() => base.OnComponentDeactivated();

    private void ApplyBonePosition(string boneName, Vector3 position, Quaternion rotation)
    {
        if (Humanoid is null)
            return;

        Humanoid.SetBonePositionAndRotation(boneName, position, rotation, true, true, true);
    }

    private readonly Queue<(string, float)> _blendshapeQueue = new();

    private void ApplyBlendshape(string blendShapeName, float value)
    {
        if (Humanoid is null)
            return;

        Humanoid.SetBlendshapeValue(blendShapeName, value);
    }

    private void ApplyBlendShapes()
    {
        while (_blendshapeQueue.TryDequeue(out var blendShape))
            ApplyBlendshape(blendShape.Item1, blendShape.Item2);
    }

    private void ApplyRootPosition(string rootName, Vector3 position, Quaternion rotation)
    {
        if (Humanoid is null)
            return;

        if (string.Equals(Humanoid.SceneNode.Name, rootName, StringComparison.InvariantCulture))
            Humanoid.SetRootPositionAndRotation(position, rotation, true, true);
        else //Fallback to regular call
            Humanoid.SetBonePositionAndRotation(rootName, position, rotation, true, true);
    }

    private void ApplyCameraTransform(Vector3 pos, Quaternion rot, float fov)
    {
        if (Camera is null)
            return;

        var cam = Camera.Camera;
        if (cam.Parameters is XRPerspectiveCameraParameters persp)
            persp.VerticalFieldOfView = fov;
        else
            cam.Parameters = new XRPerspectiveCameraParameters(fov, null, cam.Parameters.NearZ, cam.Parameters.FarZ);

        var tfm = Camera.TransformAs<Transform>(true)!;
        tfm.SetWorldTranslation(pos);
        tfm.SetWorldRotation(rot);
    }

    private void ApplyLightTransform(string name, Vector3 pos, Quaternion rot, ColorF4 color)
    {
        if (Lights is null || Lights.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.InvariantCulture)) is not DirectionalLightComponent light)
            return;

        var tfm = light.TransformAs<Transform>(true)!;
        tfm.SetWorldTranslation(pos);
        tfm.SetWorldRotation(rot);

        light.Color = new ColorF3(color.R, color.G, color.B);
        light.DiffuseIntensity = color.A;
    }

    //private async Task ReceiveDataAsync()
    //{
    //    if (Server is null)
    //        return;

    //    try
    //    {
    //        var result = await _udpClient.ReceiveAsync();
    //        string message = Encoding.UTF8.GetString(result.Buffer);
    //        ParseMessage(message);
    //    }
    //    catch (Exception ex)
    //    {
    //        Console.WriteLine($"Error receiving data: {ex.Message}");
    //    }
    //}

    //private void ParseMessage(string message)
    //{
    //    string[] lines = message.Split('\n');
    //    foreach (var line in lines)
    //    {
    //        if (string.IsNullOrWhiteSpace(line))
    //            continue;

    //        string[] parts = line.Split(' ');
    //        string address = parts[0];

    //        switch (address)
    //        {
    //            case CMD_BoneTransform:
    //                ParseBonePosition(parts);
    //                break;
    //            case CMD_BlendshapeValue:
    //                ParseBlendShape(parts);
    //                break;
    //            case CMD_BlendshapeApply:
    //                ApplyBlendShapes();
    //                break;
    //            case CMD_RootTransform:
    //                ParseRootPosition(parts);
    //                break;
    //            case CMD_CameraTransform:
    //                ParseCamera(parts);
    //                break;
    //            case CMD_LightTransform:
    //                ParseLight(parts);
    //                break;
    //            case CMD_MidiNote:
    //                ParseMidiNoteInput(parts);
    //                break;
    //            case CMD_MidiCC:
    //                ParseMidiCCValue(parts);
    //                break;
    //            case CMD_MidiCCButton:
    //                ParseMidiCCButton(parts);
    //                break;
    //            default:
    //                Console.WriteLine($"Unknown command: {line}");
    //                break;
    //        }
    //    }
    //}

    //private void ParseMidiCCButton(string[] parts)
    //{

    //}

    //private void ParseMidiCCValue(string[] parts)
    //{

    //}

    //private void ParseMidiNoteInput(string[] parts)
    //{

    //}

    //private void ParseBonePosition(string[] parts)
    //{
    //    if (parts.Length != 9)
    //        return;

    //    string boneName = parts[1];

    //    Vector3 position = new(
    //        float.Parse(parts[2]),
    //        float.Parse(parts[3]),
    //        float.Parse(parts[4]));

    //    Quaternion rotation = new(
    //        float.Parse(parts[5]),
    //        float.Parse(parts[6]),
    //        float.Parse(parts[7]),
    //        float.Parse(parts[8]));

    //    ApplyBonePosition(boneName, position, rotation);
    //}

    //private void ParseBlendShape(string[] parts)
    //{
    //    if (parts.Length != 3)
    //        return;

    //    string blendShapeName = parts[1];
    //    float value = float.Parse(parts[2]);

    //    _blendshapeQueue.Enqueue((blendShapeName, value));
    //}

    //private void ParseRootPosition(string[] parts)
    //{
    //    if (parts.Length < 8)
    //        return;

    //    string rootName = parts[1];

    //    Vector3 position = new(
    //        float.Parse(parts[2]),
    //        float.Parse(parts[3]),
    //        float.Parse(parts[4]));

    //    Quaternion rotation = new(
    //        float.Parse(parts[5]),
    //        float.Parse(parts[6]),
    //        float.Parse(parts[7]),
    //        float.Parse(parts[8]));

    //    ApplyRootPosition(rootName, position, rotation);
    //}

    //private void ParseCamera(string[] parts)
    //{
    //    if (parts.Length != 10)
    //        return;

    //    var pos = new Vector3(
    //        float.Parse(parts[2]),
    //        float.Parse(parts[3]),
    //        float.Parse(parts[4]));

    //    var rot = new Quaternion(
    //        float.Parse(parts[5]),
    //        float.Parse(parts[6]),
    //        float.Parse(parts[7]),
    //        float.Parse(parts[8]));

    //    var fov = float.Parse(parts[9]);

    //    ApplyCameraTransform(pos, rot, fov);
    //}

    //private void ParseLight(string[] parts)
    //{
    //    if (TryParseLight(parts, out Vector3 pos, out Quaternion rot, out ColorF4 color))
    //        ApplyLightTransform(pos, rot, color);
    //}

    //private static bool TryParseLight(string[] parts, out Vector3 pos, out Quaternion rot, out ColorF4 color)
    //{
    //    if (parts.Length != 13)
    //    {
    //        pos = Vector3.Zero;
    //        rot = Quaternion.Identity;
    //        color = new ColorF4(0, 0, 0, 0);
    //        return false;
    //    }

    //    pos = new Vector3(
    //        float.Parse(parts[2]),
    //        float.Parse(parts[3]),
    //        float.Parse(parts[4]));

    //    rot = new Quaternion(
    //        float.Parse(parts[5]),
    //        float.Parse(parts[6]),
    //        float.Parse(parts[7]),
    //        float.Parse(parts[8]));

    //    color = new ColorF4(
    //        float.Parse(parts[9]),
    //        float.Parse(parts[10]),
    //        float.Parse(parts[11]),
    //        float.Parse(parts[12]));

    //    return true;
    //}
}