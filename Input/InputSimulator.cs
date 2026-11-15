using System;
using System.Runtime.InteropServices;
using VibeDesk.Native;

namespace VibeDesk.Input
{
    public enum InputEventType : byte
    {
        MouseMove = 1,
        MouseDown = 2,
        MouseUp = 3,
        MouseWheel = 4,
        KeyDown = 5,
        KeyUp = 6,
        MouseRelative = 7
    }

    public enum VibeMouseButton : byte
    {
        Left = 1,
        Right = 2,
        Middle = 3,
        XButton1 = 4,
        XButton2 = 5
    }

    public static class InputSimulator
    {
        private static int _activeMonitorLeft = 0;
        private static int _activeMonitorTop = 0;
        private static int _activeMonitorWidth = 0;
        private static int _activeMonitorHeight = 0;

        public static void SetMonitorBounds(int left, int top, int width, int height)
        {
            _activeMonitorLeft = left;
            _activeMonitorTop = top;
            _activeMonitorWidth = width;
            _activeMonitorHeight = height;
        }

        public static void SendMouseRelative(int deltaX, int deltaY)
        {
            var input = new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                u = new Win32.InputUnion
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        dx = deltaX,
                        dy = deltaY,
                        dwFlags = Win32.MOUSEEVENTF_MOVE // Relative motion for 3D games and camera rotation
                    }
                }
            };

            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }
        public static void SendMouseMove(double normX, double normY)
        {
            normX = Math.Clamp(normX, 0.0, 1.0);
            normY = Math.Clamp(normY, 0.0, 1.0);

            int vLeft = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
            int vTop = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
            int vWidth = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
            int vHeight = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);

            if (vWidth <= 0) vWidth = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            if (vHeight <= 0) vHeight = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            int monWidth = _activeMonitorWidth > 0 ? _activeMonitorWidth : Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            int monHeight = _activeMonitorHeight > 0 ? _activeMonitorHeight : Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            int pixelX = _activeMonitorLeft + (int)Math.Round(normX * monWidth);
            int pixelY = _activeMonitorTop + (int)Math.Round(normY * monHeight);

            int absX = (int)Math.Round((double)(pixelX - vLeft) * 65535.0 / vWidth);
            int absY = (int)Math.Round((double)(pixelY - vTop) * 65535.0 / vHeight);

            var input = new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                u = new Win32.InputUnion
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        dx = absX,
                        dy = absY,
                        dwFlags = Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_MOVE | Win32.MOUSEEVENTF_VIRTUALDESK
                    }
                }
            };

            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        public static void SendMouseButton(VibeMouseButton button, bool isDown)
        {
            uint flag = 0;
            uint mouseData = 0;
            switch (button)
            {
                case VibeMouseButton.Left:
                    flag = isDown ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP;
                    break;
                case VibeMouseButton.Right:
                    flag = isDown ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP;
                    break;
                case VibeMouseButton.Middle:
                    flag = isDown ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP;
                    break;
                case VibeMouseButton.XButton1:
                    flag = isDown ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP;
                    mouseData = Win32.XBUTTON1;
                    break;
                case VibeMouseButton.XButton2:
                    flag = isDown ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP;
                    mouseData = Win32.XBUTTON2;
                    break;
            }

            if (flag == 0) return;

            var input = new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                u = new Win32.InputUnion
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        dwFlags = flag,
                        mouseData = mouseData
                    }
                }
            };

            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        public static void SendMouseWheel(int delta)
        {
            var input = new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                u = new Win32.InputUnion
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        mouseData = unchecked((uint)delta),
                        dwFlags = Win32.MOUSEEVENTF_WHEEL
                    }
                }
            };

            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }

        public static void SendKey(ushort virtualKeyCode, bool isDown)
        {
            uint scanCode = Win32.MapVirtualKey(virtualKeyCode, 0);
            uint flags = 0;

            if (!isDown)
            {
                flags |= Win32.KEYEVENTF_KEYUP;
            }

            // Extended keys (arrows, home, end, insert, delete, right alt/ctrl, etc.)
            if ((virtualKeyCode >= 0x21 && virtualKeyCode <= 0x2E) ||
                virtualKeyCode == 0x5B || virtualKeyCode == 0x5C || // Windows keys
                virtualKeyCode == 0x6F || // Divide
                virtualKeyCode == 0xA3 || virtualKeyCode == 0xA5)   // RCtrl, RAlt
            {
                flags |= Win32.KEYEVENTF_EXTENDEDKEY;
            }

            // Важно для 3D-игр (DirectInput, RawInput, Genshin Impact, Unity, Unreal):
            // Игровые движки считывают аппаратные скан-коды клавиш (KEYEVENTF_SCANCODE)
            if (scanCode > 0)
            {
                flags |= Win32.KEYEVENTF_SCANCODE;
            }

            var input = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = (flags & Win32.KEYEVENTF_SCANCODE) != 0 ? (ushort)0 : virtualKeyCode,
                        wScan = (ushort)scanCode,
                        dwFlags = flags
                    }
                }
            };

            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }
    }
}
