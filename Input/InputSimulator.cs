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
        KeyUp = 6
    }

    public enum VibeMouseButton : byte
    {
        Left = 1,
        Right = 2,
        Middle = 3
    }

    public static class InputSimulator
    {
        public static void SendMouseMove(double normX, double normY)
        {
            // Normalize to 0 .. 65535 coordinate space
            normX = Math.Clamp(normX, 0.0, 1.0);
            normY = Math.Clamp(normY, 0.0, 1.0);

            int absX = (int)Math.Round(normX * 65535.0);
            int absY = (int)Math.Round(normY * 65535.0);

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
            }

            if (flag == 0) return;

            var input = new Win32.INPUT
            {
                type = Win32.INPUT_MOUSE,
                u = new Win32.InputUnion
                {
                    mi = new Win32.MOUSEINPUT
                    {
                        dwFlags = flag
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

            var input = new Win32.INPUT
            {
                type = Win32.INPUT_KEYBOARD,
                u = new Win32.InputUnion
                {
                    ki = new Win32.KEYBDINPUT
                    {
                        wVk = virtualKeyCode,
                        wScan = (ushort)scanCode,
                        dwFlags = flags
                    }
                }
            };

            Win32.SendInput(1, new[] { input }, Marshal.SizeOf<Win32.INPUT>());
        }
    }
}
