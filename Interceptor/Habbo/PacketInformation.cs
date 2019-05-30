using System;
using System.Collections.Generic;
using System.Text;

namespace Interceptor.Habbo
{
    public enum PacketValue : byte
    {
        Unknown,
        Short,
        Integer,
        String,
        Boolean,
        Byte,
        Double
    }

    public struct PacketInformation
    {
        public ushort Id { get; }
        public string Hash { get; }
        public PacketValue[] Structure { get; }

        public PacketInformation(ushort id, string hash = null, PacketValue[] structure = null)
        {
            Id = id;
            Hash = hash?.Substring(0, 6) ?? string.Empty;
            Structure = structure;
        }
    }
}
