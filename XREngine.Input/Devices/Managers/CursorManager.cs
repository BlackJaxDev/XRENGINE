using System.Drawing;
using System.Numerics;

namespace XREngine.Input.Devices
{
    public delegate void DelCursorUpdate(float x, float y);
    [Serializable]
    public class CursorManager : InputManagerBase
    {
        private float _lastX, _lastY;

        //relative, absolute
        private readonly List<DelCursorUpdate?>?[] _onCursorUpdate = new List<DelCursorUpdate?>?[2];

        public void Register(DelCursorUpdate func, EMouseMoveType type, bool unregister)
        {
            int index = (int)type;
            if (unregister)
            {
                List<DelCursorUpdate?>? list = _onCursorUpdate[index];
                if (list is null)
                    return;

                list.Remove(func);
                if (list.Count == 0)
                    _onCursorUpdate[index] = null;
            }
            else
            {
                if (_onCursorUpdate[index] is null)
                    _onCursorUpdate[index] = [func];
                else
                    _onCursorUpdate[index]?.Add(func);
            }
        }
        //public Rectangle? WrapBounds { get; set; } = null;
        protected internal void Tick(float x, float y)
            => Tick(x, y, null, 0);

        internal void Tick(float x, float y, InputDevice? device, ulong revision)
        {
            float dX, dY;
            //if (WrapBounds is not null)
            //{
            //    Vector2 position = new(x, y);
            //    Vector2 lastPosition = new(_lastX, _lastY);
            //    position = Wrap(position, lastPosition, WrapBounds.Value, out dX, out dY);
            //    x = position.X;
            //    y = position.Y;
            //}
            //else
            //{
                dX = x - _lastX;
                dY = y - _lastY;
            //}
            ExecuteList(x, y, _onCursorUpdate[(int)EMouseMoveType.Absolute], device, revision);
            if (device is null || revision == device.InputDispatchRevision)
                ExecuteList(dX, -dY, _onCursorUpdate[(int)EMouseMoveType.Relative], device, revision);
            _lastX = x;
            _lastY = y;
        }
        //protected internal void TickRelative(float dX, float dY)
        //{
        //    float x = _lastX + dX;
        //    float y = _lastY + dY;
        //    if (WrapBounds is not null)
        //    {
        //        Vector2 position = new(x, y);
        //        Vector2 lastPosition = new(_lastX, _lastY);
        //        position = Wrap(position, lastPosition, WrapBounds.Value, out dX, out dY);
        //        x = position.X;
        //        y = position.Y;
        //    }
        //    PerformAction(EMouseMoveType.Absolute, x, y);
        //    PerformAction(EMouseMoveType.Relative, dX, dY);
        //    _lastX = x;
        //    _lastY = y;
        //}
        protected void PerformAction(EMouseMoveType type, float x, float y)
        {
            int index = (int)type;
            ExecuteList(x, y, _onCursorUpdate[index]);
            //Engine.DebugPrint(_name + ": " + type.ToString());
        }

        private static void ExecuteList(float x, float y, List<DelCursorUpdate?>? list)
            => ExecuteList(x, y, list, null, 0);

        private static void ExecuteList(float x, float y, List<DelCursorUpdate?>? list, InputDevice? device, ulong revision)
        {
            if (list is null)
                return;

            try
            {
                for (int i = 0; i < list.Count && (device is null || revision == device.InputDispatchRevision); i++)
                    list[i]?.Invoke(x, y);
            }
            catch (Exception)
            {
                //Output.LogException(e);
            }
        }

        public static Vector2 Wrap(Vector2 position, Vector2 lastPosition, Rectangle bounds, out float relX, out float relY)
        {
            //Wrap the X-coord of the cursor
            if (position.X >= bounds.Right - 1)
            {
                while (position.X >= bounds.Right - 1)
                    position.X -= bounds.Width;

                position.X += 1;
                relX = (position.X - bounds.Left) + (bounds.Right - 1 - lastPosition.X);
            }
            else if (position.X <= bounds.Left)
            {
                while (position.X <= bounds.Left)
                    position.X += bounds.Width;

                position.X -= 1;
                relX = (position.X - (bounds.Right - 1)) + (bounds.Left - lastPosition.X);
            }
            else
            {
                relX = position.X - lastPosition.X;
            }

            //Wrap the Y-coord of the cursor
            if (position.Y >= bounds.Bottom - 1)
            {
                while (position.Y >= bounds.Bottom - 1)
                    position.Y -= bounds.Height;

                position.Y += 1;
                relY = (position.Y - bounds.Top) + (bounds.Bottom - 1 - lastPosition.Y);
            }
            else if (position.Y <= bounds.Top)
            {
                while (position.Y <= bounds.Top)
                    position.Y += bounds.Height;

                position.Y -= 1;
                relY = (position.Y - (bounds.Bottom - 1)) + (bounds.Top - lastPosition.Y);
            }
            else
            {
                relY = lastPosition.Y - position.Y;
            }

            return position;
        }
    }
}
