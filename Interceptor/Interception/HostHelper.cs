using System;
using System.IO;
using System.Net;
using System.Linq;

namespace Interceptor.Interception
{
    public static class HostHelper
    {
        private static string HostsFilePath { get; }

        static HostHelper()
        {
            HostsFilePath = Path.Combine(Environment.SystemDirectory, "drivers\\etc\\hosts");
            TryRemoveRedirects();
        }

        public static bool TryAddRedirect(string original, string redirect)
        {
            try
            {
                string match = $"{original} {redirect}";
                if (!File.ReadAllLines(HostsFilePath).Any(l => l.Contains(match)))
                    File.AppendAllText(HostsFilePath, $"\r\n{original} {redirect} #HabboInterceptor");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryRemoveRedirects()
        {
            try
            {
                File.WriteAllLines(HostsFilePath, File.ReadAllLines(HostsFilePath).Where(l => !l.EndsWith("#HabboInterceptor") && l != string.Empty));
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
