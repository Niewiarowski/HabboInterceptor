using System;
using System.Linq;
using System.Management;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Interceptor.Encryption;
using System.Security.Cryptography;

namespace Interceptor.Memory
{
    public static class RC4Extractor
    {
        [DllImport("kernel32.dll")]
        private static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MemoryPage lpBuffer, uint dwLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(uint dwAccess, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, long lpBaseAddress, [In, Out] byte[] lpBuffer, UInt64 dwSize, out IntPtr lpNumberOfBytesRead);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryPage
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public int AllocationProtect;
            private int __alignment1;
            public ulong RegionSize;
            public int State;
            public int Protect;
            public int Type;
            private int __alignment2;
        }

        private static IntPtr CurrentHandle { get; set; }

        public static bool TryExtractKey(out RC4Key[] key) => (key = ExtractKey()).Length != 0;
        public static RC4Key[] ExtractKey()
        {
            Process process = GetProcess();
            if (process != null)
            {
                CurrentHandle = OpenProcess(0x0010 | 0x0008 | 0x0020, false, process.Id);
                if (CurrentHandle != IntPtr.Zero)
                {
                    List<RC4Key> keys = new List<RC4Key>(2);

                    foreach (MemoryPage page in GetMemoryPages())
                    {
                        List<Memory<byte>> potentialKeyBytes = FindRC4Key(page);
                        if (potentialKeyBytes != null)
                            foreach (Memory<byte> rc4Key in potentialKeyBytes)
                                if (!rc4Key.IsEmpty)
                                    keys.Add(RC4Key.Copy(rc4Key.Span));
                    }

                    CloseHandle(CurrentHandle);
                    return keys.ToArray();
                }
            }

            return null;
        }

        private static Process GetProcess()
        {
            static string GetCommandLine(Process process)
            {
                using ManagementObjectSearcher searcher = new ManagementObjectSearcher(string.Concat("SELECT CommandLine FROM Win32_Process WHERE ProcessId = ", process.Id));
                using ManagementObjectCollection objects = searcher.Get();
                return objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
            }

            return Process.GetProcessesByName("chrome")
                .Union(Process.GetProcessesByName("plugin-container"))
                .OrderByDescending(p => p.StartTime)
                .FirstOrDefault(p => p.ProcessName == "plugin-container" || GetCommandLine(p).Contains("ppapi"));
        }

        private static IEnumerable<MemoryPage> GetMemoryPages()
        {
            ulong address = 0;
            uint size = (uint)Marshal.SizeOf(typeof(MemoryPage));

            do
            {
                // Should only work for 64-bit processes
                if (VirtualQueryEx(CurrentHandle, (IntPtr)address, out MemoryPage page, size) == 0 || address == page.BaseAddress + page.RegionSize)
                    break;

                address = page.BaseAddress + page.RegionSize;
                yield return page;
            }
            while (address <= 0x00007fffffffffff);
        }

        private static double Entropy(Span<byte> data)
        {
            Span<int> values = stackalloc int[256];

            for (int i = data.Length; --i >= 0;)
                values[data[i]]++;

            double H = 0.0;
            double cb = data.Length;
            for(int i = 256; --i >= 0;)
            {
                int value = values[i];
                if (value > 0)
                    H += value * Math.Log(value / cb, 2.0);
            }

            return -H / cb;
        }


        private static List<Memory<byte>> FindRC4Key(MemoryPage memoryPage)
        {
            if (memoryPage.RegionSize > 1024 && memoryPage.Protect == 4 && memoryPage.Type == 131072 && memoryPage.AllocationProtect == 1)
            {
                ulong bytesRead = 0;
                int lastValue = -1;
                byte[] page = new byte[Math.Min(memoryPage.RegionSize, 1000000)];
                Span<int> pageSpan = MemoryMarshal.Cast<byte, int>(page);
                Span<byte> realKey = stackalloc byte[256];

                List<Memory<byte>> keys = new List<Memory<byte>>();

                do
                {
                    if (ReadProcessMemory(CurrentHandle, (long)memoryPage.BaseAddress + (long)bytesRead, page, Math.Min((ulong)page.Length, memoryPage.RegionSize - bytesRead), out IntPtr numBytesRead))
                    {
                        bool validKey = false;
                        for (int i = 0; i < pageSpan.Length - 256; i++)
                        {
                            int maxK = i + 256;
                            for (int kIndex = 0, k = i; k < maxK; k++, kIndex++)
                            {
                                int value = pageSpan[k];
                                if (value > 255 || value < 0 || (lastValue == 0 && (value == 0 || value == 1)))
                                {
                                    lastValue = -1;
                                    validKey = false;
                                    break;
                                }
                                else
                                {
                                    lastValue = value;
                                    realKey[kIndex] = (byte)value;
                                    validKey = true;
                                }
                            }

                            if (validKey && Entropy(realKey) > 7)
                                keys.Add(realKey.ToArray());
                        }
                    }

                    bytesRead += (ulong)numBytesRead;
                }
                while (bytesRead < memoryPage.RegionSize);

                return keys;
            }


            return null;
        }
    }
}
