using System;
using System.Runtime.InteropServices;

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

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct PacketInformation
    {
        public ushort Id { get; }
        public ReadOnlyMemory<char> Hash { get; }
        public PacketValue[] Structure { get; }

        public PacketInformation(ushort id, string hash = null, PacketValue[] structure = null)
        {
            Id = id;
            Hash = hash?.AsMemory().Slice(0, 6) ?? default; // ?? validate nulls, so... object ?? null <- is weird (if it's null, then set it null?)
            Structure = structure;
        }
    }
}
