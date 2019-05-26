using System;
using System.Collections.Generic;
using System.Text;

namespace Interceptor.Habbo
{
    public class PacketInformation
    {
        public ushort Id { get; }
        public string Hash { get; }
        public string[] Structure { get; }

        public PacketInformation(ushort id, string hash = null, string[] structure = null)
        {
            Id = id;
            Hash = hash ?? string.Empty;
            Structure = structure;
        }
    }
}
