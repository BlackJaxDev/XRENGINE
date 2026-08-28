//using System;
//using System.Drawing;
//using System.Windows.Forms;
//using XREngine;
//using XREngine.Core.Maths;
//using XREngine.Timers;

//namespace Extensions
//{
//    public static class ControlExtension // Editor-only legacy control helpers.
//    {
//        public static void LerpLocation(
//            this Control control,
//            Point point,
//            float seconds,
//            ref EventHandler<FrameEventArgs> fadeMethod,
//            Func<float, float> timeModifier = null,
//            Action onComplete = null)
//        {
//            Point startPoint = control.Location;
//            float totalTime = 0.0f;
//            if (fadeMethod != null)
//            {
//                Engine.UnregisterRenderTick(null, fadeMethod, null);
//                fadeMethod = null;
//            }
//            if (timeModifier != null)
//            {
//                void method(object sender, FrameEventArgs args)
//                {
//                    totalTime += args.Time;
//                    control.InvokeIfNecessary((Action)(() => control.Location = Interp.Lerp(startPoint, point, timeModifier(totalTime / seconds))), null);
//                    if (totalTime >= seconds)
//                    {
//                        Engine.UnregisterRenderTick(null, method, null);
//                        onComplete?.Invoke();
//                    }
//                }
//                fadeMethod = method;
//                Engine.RegisterRenderTick(null, method, null);
//            }
//            else
//            {
//                void method(object sender, FrameEventArgs args)
//                {
//                    totalTime += args.Time;
//                    control.Location = Interp.Lerp(startPoint, point, totalTime / seconds);
//                    if (totalTime >= seconds)
//                        Engine.UnregisterRenderTick(null, method, null);
//                }
//                fadeMethod = method;
//                Engine.RegisterRenderTick(null, method, null);
//            }
//        }
//        public static void FadeBackColor(this Control control, Color color, float seconds, ref EventHandler<FrameEventArgs> fadeMethod, Func<float, float> timeModifier = null)
//        {
//            Color startColor = control.BackColor;
//            float totalTime = 0.0f;
//            if (fadeMethod != null)
//            {
//                Engine.UnregisterRenderTick(null, fadeMethod, null);
//                fadeMethod = null;
//            }
//            if (timeModifier != null)
//            {
//                void method(object sender, FrameEventArgs args)
//                {
//                    totalTime += args.Time;
//                    control.BackColor = Interp.Lerp(startColor, color, timeModifier(totalTime / seconds));
//                    if (totalTime >= seconds)
//                        Engine.UnregisterRenderTick(null, method, null);
//                }
//                fadeMethod = method;
//                Engine.RegisterRenderTick(null, method, null);
//            }
//            else
//            {
//                void method(object sender, FrameEventArgs args)
//                {
//                    totalTime += args.Time;
//                    control.BackColor = Interp.Lerp(startColor, color, totalTime / seconds);
//                    if (totalTime >= seconds)
//                        Engine.UnregisterRenderTick(null, method, null);
//                }
//                fadeMethod = method;
//                Engine.RegisterRenderTick(null, method, null);
//            }
//        }
//        public static void FadeForeColor(this Control control, Color color, float seconds, ref EventHandler<FrameEventArgs> fadeMethod, Func<float, float> timeModifier = null)
//        {
//            Color startColor = control.ForeColor;
//            float totalTime = 0.0f;
//            if (fadeMethod != null)
//            {
//                Engine.UnregisterRenderTick(null, fadeMethod, null);
//                fadeMethod = null;
//            }
//            if (timeModifier != null)
//            {
//                void method(object sender, FrameEventArgs args)
//                {
//                    totalTime += args.Time;
//                    control.ForeColor = Interp.Lerp(startColor, color, timeModifier(totalTime / seconds));
//                    if (totalTime >= seconds)
//                        Engine.UnregisterRenderTick(null, method, null);
//                }
//                fadeMethod = method;
//                Engine.RegisterRenderTick(null, method, null);
//            }
//            else
//            {
//                void method(object sender, FrameEventArgs args)
//                {
//                    totalTime += args.Time;
//                    control.ForeColor = Interp.Lerp(startColor, color, totalTime / seconds);
//                    if (totalTime >= seconds)
//                        Engine.UnregisterRenderTick(null, method, null);
//                }
//                fadeMethod = method;
//                Engine.RegisterRenderTick(null, method, null);
//            }
//        }
//    }
//}
