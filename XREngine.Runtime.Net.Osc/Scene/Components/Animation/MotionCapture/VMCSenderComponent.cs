namespace XREngine.Components;

public class VMCSenderComponent : VMCComponent
{
    public OscCore.OscClient? Client { get; private set; } = null;

    private const string CMD_FramePeriod = "/VMC/Ext/Set/Period"; //int status, int root, int blendshape, int camera, int devices
    //Sets the send period of each data type. value = 1/x frame
    
    private const string CMD_Request = "/VMC/Ext/Set/Req";
    //Request immediate data transmission

    private const string CMD_Response = "/VMC/Ext/Set/Res"; //string message
    //General-puspose message (sender arbitrary definition)

    private const string CMD_CalibrationReady = "/VMC/Ext/Set/Calib/Ready";
    private const string CMD_CalibrationExecute = "/VMC/Ext/Set/Calib/Exec"; //int mode
    //Ready: request calibration ready
    //Execute: execute calibration (mode: 0=normal, 1=mr normal, 2=mr floor fix)

    private const string RequestLoadSetting = "/VMC/Ext/Set/Config"; //string path

    private const string CMD_CallShortcut = "/VMC/Ext/Set/Shortcut"; //string name
    //Call a shortcut registered in the VMC settings
    //Example:
    // /VMC/Ext/Set/Shortcut Functions.FreeCamera
}
