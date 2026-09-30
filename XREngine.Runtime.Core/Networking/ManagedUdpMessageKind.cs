namespace XREngine.Networking;

public enum ManagedUdpMessageKind : byte { Hello = 1, Challenge = 2, Commit = 3, Accept = 4, Data = 5, Reject = 6, Close = 7 }
