// These declarations exercise generator support without adding unused runtime contracts.
// Tests install them explicitly and verify that disposal removes their registrations.
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Collections.Generic.List<XREngine.Data.Core.XRPersistentCall>))]
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.Collections.Generic.HashSet<string>))]
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.DateTime?))]
[assembly: XREngine.RuntimeClosedFormatter(typeof(System.ValueTuple<int, int>))]
[assembly: XREngine.RuntimeClosedFormatter(typeof(long[]))]
