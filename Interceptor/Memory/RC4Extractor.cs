using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

using Interceptor.Encryption;

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

        public static bool TryExtractKey(out RC4Key key) => (key = ExtractKey()) != null;
        public static RC4Key ExtractKey()
        {
            Process process = GetProcess();
            if(process != null)
            {
                CurrentHandle = OpenProcess(0x0010 | 0x0008 | 0x0020, false, process.Id);
                if(CurrentHandle != IntPtr.Zero)
                {
                    Memory<byte> rc4Key = default;
                    foreach(MemoryPage page in GetMemoryPages())
                    {
                        rc4Key = FindRC4Key(page);
                        if (!rc4Key.IsEmpty)
                            break;
                    }

                    CloseHandle(CurrentHandle);
                    return rc4Key.IsEmpty ? null : new RC4Key(rc4Key.Span);
                }
            }

            return null;
        }

        private static Process GetProcess()
        {
            static string GetCommandLine(Process process)
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(string.Concat("SELECT CommandLine FROM Win32_Process WHERE ProcessId = ", process.Id)))
                using (ManagementObjectCollection objects = searcher.Get())
                    return objects.Cast<ManagementBaseObject>().SingleOrDefault()?["CommandLine"]?.ToString();
            }

            return Process.GetProcessesByName("chrome").OrderByDescending(p => p.StartTime).FirstOrDefault(p => GetCommandLine(p).Contains("ppapi"));
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

        private static Memory<byte> FindRC4Key(MemoryPage memoryPage)
        {
            if (memoryPage.RegionSize > 1024 && memoryPage.RegionSize < 4000000 && memoryPage.Protect == 4 && memoryPage.Type == 131072 && memoryPage.AllocationProtect == 1)
            {
                ulong bytesRead = 0;
                byte[] page = new byte[Math.Min(memoryPage.RegionSize, 1000000)];
                Span<byte> realKey = stackalloc byte[256];
                Span<byte> repeats = stackalloc byte[256];

                do
                {
                    if (ReadProcessMemory(CurrentHandle, (long)memoryPage.BaseAddress, page, (ulong)page.Length, out IntPtr numBytesRead))
                    {
                        bool validKey = false;
                        for (int i = 0; i < page.Length - 1024; i += 4)
                        {
                            for (int k = 0; k < Math.Min(page.Length - i + k, 1024); k++)
                            {
                                byte value = page[k + i];
                                bool keyByte = k % 4 == 0;
                                if ((!keyByte && value != 0) || (keyByte && repeats[value]++ > 3))
                                {
                                    repeats.Fill(0);
                                    validKey = false;
                                    break;
                                }
                                else if (keyByte)
                                {
                                    realKey[k / 4] = value;
                                    validKey = true;
                                }
                            }

                            if (validKey)
                                return realKey.ToArray();
                        }
                    }

                    bytesRead += (ulong)numBytesRead;
                }
                while (bytesRead < memoryPage.RegionSize);
            }

            return null;
        }
    }
}
