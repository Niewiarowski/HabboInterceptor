using System;
using System.Collections.Generic;
using System.Text;

namespace Interceptor.Attributes
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class PacketAttribute : Attribute
    {
        public ushort Header { get; }
        public ReadOnlyMemory<char> Hash { get; }

        public PacketAttribute(ushort header)
        {
            Header = header;
        }

        public PacketAttribute(string hash)
        {
            Hash = hash.AsMemory();
        }
    }
}
