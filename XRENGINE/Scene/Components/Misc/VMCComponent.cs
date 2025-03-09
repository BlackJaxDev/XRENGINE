using System.Net.Sockets;
using XREngine.Components;

namespace XREngine.Scene.Components;

/// <summary>
/// Base class for all VMC protocol components.
/// </summary>
public abstract class VMCComponent : XRComponent
{
    public const int VMC_Port = 39539; // Common VMC Protocol port

    protected const string CMD_HMDPos = "/VMC/Ext/Hmd/Pos"; //string serial, float x, float y, float z, float qx, float qy, float qz, float qw
    protected const string CMD_ControllerPos = "/VMC/Ext/Con/Pos"; //string serial, float x, float y, float z, float qx, float qy, float qz, float qw
    protected const string CMD_TrackerPos = "/VMC/Ext/Tra/Pos"; //string serial, float x, float y, float z, float qx, float qy, float qz, float qw
    protected const string CMD_HMDPosLocal = "/VMC/Ext/Hmd/Pos/Local"; //string serial, float x, float y, float z, float qx, float qy, float qz, float qw
    protected const string CMD_ControllerPosLocal = "/VMC/Ext/Con/Pos/Local"; //string serial, float x, float y, float z, float qx, float qy, float qz, float qw
    protected const string CMD_TrackerPosLocal = "/VMC/Ext/Tra/Pos/Local"; //string serial, float x, float y, float z, float qx, float qy, float qz, float qw
    //Pos = avatar scale
    //Pos/Local = device raw scale
    //serial = OpenVR serial number
    //x, y, z = position
    //qx, qy, qz, qw = rotation

    protected const string CMD_MidiNote = "/VMC/Ext/Midi/Note"; //int active, int channel, int note, float velocity
    //active: 1=pressed, 0=released
    protected const string CMD_MidiCC = "/VMC/Ext/Midi/CC/Val"; //int knob, float value
    protected const string CMD_MidiCCButton = "/VMC/Ext/Midi/CC/Bit"; //int knob, int active
    //active: 1=pressed, 0=released
    
    protected const string CMD_CameraTransform = "/VMC/Ext/Cam"; //string name, float x, float y, float z, float qx, float qy, float qz, float qw, float fov
    //Perspective camera position, rotation, and field of view
    
    protected const string CMD_BlendshapeValue = "/VMC/Ext/Blend/Val"; //string name, float value
    protected const string CMD_BlendshapeApply = "/VMC/Ext/Blend/Apply"; //Called after all blend shape values are set by CMD_BlendshapeValue

    protected const string CMD_Thru = "/VMC/Thru/"; //string arg1, float/int arg2

    protected const string CMD_LightTransform = "/VMC/Ext/Light"; //string name, float x, float y, float z, float qx, float qy, float qz, float qw, float r, float g, float b, float a
    //Directional light position, rotation, and color
}
