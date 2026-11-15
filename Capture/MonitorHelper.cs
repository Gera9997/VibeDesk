using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VibeDesk.Native;

namespace VibeDesk.Capture
{
    public class DisplayMonitorInfo
    {
        public int Index { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsPrimary { get; set; }
        public string DeviceName { get; set; } = "";
    }

    public static class MonitorHelper
    {
        public static List<DisplayMonitorInfo> GetAllMonitors()
        {
            var list = new List<DisplayMonitorInfo>();
            int idx = 0;

            try
            {
                Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Win32.RECT rc, IntPtr data) =>
                {
                    var mi = new Win32.MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf<Win32.MONITORINFOEX>();
                    if (Win32.GetMonitorInfo(hMon, ref mi))
                    {
                        list.Add(new DisplayMonitorInfo
                        {
                            Index = idx++,
                            Left = mi.rcMonitor.Left,
                            Top = mi.rcMonitor.Top,
                            Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                            Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                            IsPrimary = (mi.dwFlags & 1) != 0,
                            DeviceName = mi.szDevice ?? $"Monitor {idx}"
                        });
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            if (list.Count == 0)
            {
                int w = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
                int h = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
                list.Add(new DisplayMonitorInfo
                {
                    Index = 0,
                    Left = 0,
                    Top = 0,
                    Width = w > 0 ? w : 1920,
                    Height = h > 0 ? h : 1080,
                    IsPrimary = true,
                    DeviceName = "Primary"
                });
            }

            return list;
        }

        public static int GetMonitorCount()
        {
            var monitors = GetAllMonitors();
            return monitors.Count;
        }

        public static DisplayMonitorInfo GetMonitor(int index)
        {
            var monitors = GetAllMonitors();
            if (index >= 0 && index < monitors.Count)
            {
                return monitors[index];
            }
            return monitors[0];
        }
    }
}
