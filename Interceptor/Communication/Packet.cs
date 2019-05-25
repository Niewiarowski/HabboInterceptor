using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Interceptor.Communication
{
    public class Packet
    {
        public int Length { get; }
        internal int ConstructLength => Length + 6;
        public short Header { get; }
        public ReadOnlyMemory<byte> Bytes => _bytes;
        public bool Blocked { get; set; }

        private int _position;
        public int Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _bytes.Length);
        }

        private Memory<byte> _bytes { get; }

        public Packet(Memory<byte> bytes) : this(bytes.Span) { }
        public Packet(Span<byte> bytes) : this(bytes, out _, 0) { }
        private Packet(Span<byte> bytes, out int remainderIndex, int index)
        {
            remainderIndex = -1;
            if (bytes.Length - index >= 6)
            {
                Span<byte> header = stackalloc byte[6];
                bytes.Slice(index, 6).CopyTo(header);
                Span<byte> lengthSlice = header.Slice(0, 4);
                Span<byte> headerSlice = header.Slice(4, 2);
                if (BitConverter.IsLittleEndian)
                {
                    lengthSlice.Reverse();
                    headerSlice.Reverse();
                }

                Length = BitConverter.ToInt32(lengthSlice) - 2;
                if (Length < 0 || Length > 100000)
                {
                    remainderIndex = -2;
                    return;
                }
                Header = BitConverter.ToInt16(headerSlice);
                _bytes = bytes.Slice(6 + index, Length).ToArray();
                if (bytes.Length > index + Length + 6)
                    remainderIndex = index + Length + 6;
            }
        }

        public Packet(int length, Memory<byte> bytes) : this(length, bytes.Span) { }
        public Packet(int length, Span<byte> bytes)
        {
            Length = length - 2;
            if (length == bytes.Length)
            {
                Span<byte> header = stackalloc byte[2];
                bytes.Slice(0, 2).CopyTo(header);
                if (BitConverter.IsLittleEndian)
                    header.Reverse();
                Header = BitConverter.ToInt16(header);

                _bytes = bytes.Slice(2).ToArray();
            }
        }

        public Packet(int length, short header, Span<byte> bytes)
        {
            Length = length - 2;
            Header = header;
            _bytes = bytes.ToArray();
        }

        public static Packet[] Parse(Memory<byte> bytes) => Parse(bytes.Span);
        public static Packet[] Parse(Span<byte> bytes)
        {
            List<Packet> result = new List<Packet>();
            int remainderIndex = 0;
            while (remainderIndex >= 0)
                result.Add(new Packet(bytes, out remainderIndex, remainderIndex));

            if (remainderIndex == -2) return new Packet[0];
            return result.ToArray();
        }

        internal void ConstructTo(Span<byte> finalPacket)
        {
            Span<byte> lengthSlice = finalPacket.Slice(0, 4);
            Span<byte> headerSlice = finalPacket.Slice(4, 2);
            Span<byte> payloadSlice = finalPacket.Slice(6);
            BitConverter.GetBytes(Length + 2).CopyTo(lengthSlice);
            BitConverter.GetBytes(Header).CopyTo(headerSlice);
            _bytes.Span.CopyTo(payloadSlice);

            if (BitConverter.IsLittleEndian)
            {
                lengthSlice.Reverse();
                headerSlice.Reverse();
            }
        }

        internal Memory<byte> Construct()
        {
            Memory<byte> finalPacket = new byte[Length + 6];
            ConstructTo(finalPacket.Span);
            return finalPacket;
        }

        public void Read(Span<byte> buffer)
        {
            if (Position + buffer.Length > _bytes.Length)
                return;

            _bytes.Span.Slice(Position, buffer.Length).CopyTo(buffer);
            Position += buffer.Length;
        }

        public T Read<T>() where T : struct
        {
            T result = default;
            Span<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
            Read(span);
            if (BitConverter.IsLittleEndian && (result is int || result is short || result is long))
                span.Reverse();

            return result;
        }

        public string ReadString()
        {
            short length = Read<short>();
            if (Position + length > _bytes.Length)
                return null;

            string result = Encoding.ASCII.GetString(_bytes.Span.Slice(Position, length));
            Position += length;
            return result;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("{{l:{0}}}{{h:{1}}}: ", Length, Header);
            Span<byte> payloadSpan = _bytes.Span;
            for (int i = 0; i < _bytes.Length; i++)
            {
                byte value = payloadSpan[i];
                if (value <= 13)
                    sb.AppendFormat("[{0}]", value);
                else
                    sb.Append((char)value);
            }

            return sb.ToString();
        }
    }
}
