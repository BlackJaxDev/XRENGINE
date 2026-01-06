namespace XREngine.Data.Core
{
    public enum InvokeBoolType
    {
        //All listeners must return true for the invoke event to return true
        All,
        //Any listener can return true for the invoke event to return true
        Any
    }
}
