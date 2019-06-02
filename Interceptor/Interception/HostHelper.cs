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
            TryRemoveRedirects();
        }

        public static bool TryAddRedirect(string original, string redirect)
        {
            try
            {
                string match = string.Format("{0} {1}", original, redirect);
                if (!File.ReadAllLines(HostsFilePath).Any(l => l.Contains(match)))
                    File.AppendAllText(HostsFilePath, string.Format("\r\n{0} {1} #{2}", original, redirect, "HabboInterceptor"));
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
