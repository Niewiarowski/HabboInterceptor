using System;
using System.IO;
using System.Linq;
using System.Net;

namespace Interceptor.Interception
{
    public static class HostHelper
    {
        private static string HostsFilePath { get; }

        static HostHelper()
        {
            HostsFilePath = Path.Combine(Environment.SystemDirectory, "drivers\\etc\\hosts");
        }

        // Should make this class use Span<T>

        public static bool TryAddRedirect(string original, string redirect, string comment = "HabboInterceptor")
        {
            try
            {
                string match = string.Format("{0} {1}", original, redirect);
                if (!File.ReadAllLines(HostsFilePath).Any(l => l.Contains(match)))
                    File.AppendAllText(HostsFilePath, string.Format("\r\n{0} {1} #{2}", original, redirect, comment));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryRemoveRedirect(string original, string redirect)
        {
            try
            {
                string match = string.Format("{0} {1}", original, redirect);
                File.WriteAllLines(HostsFilePath, File.ReadAllLines(HostsFilePath).Where(l => !l.Contains(match) && l != string.Empty));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static IPAddress GetIPAddressFromHost(string host)
        {
            return Dns.GetHostEntry(host).AddressList.FirstOrDefault();
        }
    }
}
