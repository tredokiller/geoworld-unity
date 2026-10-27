using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GeoWorld
{
    // The memory of this process as the system counts it (working set, committed private bytes and their peaks): Mono's
    // System.Diagnostics.Process does not give them in a player. Windows only; elsewhere false.
    public static class GeoProcessMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Counters
        {
            public uint cb, pageFaultCount;
            public UIntPtr peakWorkingSetSize, workingSetSize, quotaPeakPagedPoolUsage, quotaPagedPoolUsage,
                quotaPeakNonPagedPoolUsage, quotaNonPagedPoolUsage, pagefileUsage, peakPagefileUsage;
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
        static extern bool GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);

        public static bool Read(out long workingSet, out long peakWorkingSet, out long privateBytes, out long peakPrivateBytes)
        {
            workingSet = peakWorkingSet = privateBytes = peakPrivateBytes = 0;
            var p = Application.platform;
            if (p != RuntimePlatform.WindowsPlayer && p != RuntimePlatform.WindowsEditor && p != RuntimePlatform.WindowsServer) return false;
            try
            {
                if (!GetProcessMemoryInfo(GetCurrentProcess(), out var c, (uint)Marshal.SizeOf<Counters>())) return false;
                workingSet = (long)c.workingSetSize; peakWorkingSet = (long)c.peakWorkingSetSize;
                privateBytes = (long)c.pagefileUsage; peakPrivateBytes = (long)c.peakPagefileUsage;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
