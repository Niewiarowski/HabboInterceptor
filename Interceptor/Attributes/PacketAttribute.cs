using System;
using System.Collections.Generic;
using System.Text;

namespace Interceptor.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class PacketAttribute : Attribute
    {
        public ulong Hash { get; }
        public ushort Header { get; }

        public PacketAttribute(ulong hash = 0, ushort header = 0)
        {
            Hash = hash;
            Header = header;
        }
    }
}
