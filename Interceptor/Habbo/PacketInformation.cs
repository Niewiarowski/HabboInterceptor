using System;
using System.Collections.Generic;
using System.Text;

namespace Interceptor.Habbo
{
    public enum PacketValue : byte
    {
        Unknown,
        Short,
        Int,
        String,
        Boolean,
        Byte,
        Double,
        Array
    }

    public class PacketInformation
    {
        public ushort Id { get; }
        public string Hash { get; }
        public PacketValue[] Structure { get; }

        public PacketInformation(ushort id, string hash = null, PacketValue[] structure = null)
        {
            Id = id;
            Hash = hash ?? string.Empty;
            Structure = structure;
        }
    }
}
