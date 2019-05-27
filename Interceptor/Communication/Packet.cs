using Interceptor.Habbo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Interceptor.Communication
{
    public class Packet
    {
        public int Length { get; private set;  }
        internal int ConstructLength => Length + 6;
        public ushort Header { get; }
        public ReadOnlyMemory<byte> Bytes => _bytes;
        public bool Blocked { get; set; }
        public string Hash { get; internal set; }
        public PacketValue[] Structure { get; internal set; }

        private int _position;
        public int Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _bytes.Length);
        }

        private Memory<byte> _bytes { get; set; }

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
                Header = BitConverter.ToUInt16(headerSlice);
                _bytes = bytes.Slice(6 + index, Length).ToArray();
                if (bytes.Length > index + ConstructLength)
                    remainderIndex = index + ConstructLength;
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
                Header = BitConverter.ToUInt16(header);

                _bytes = bytes.Slice(2).ToArray();
            }
        }

        public Packet(int length, ushort header, Span<byte> bytes)
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
            Memory<byte> finalPacket = new byte[ConstructLength];
            ConstructTo(finalPacket.Span);
            return finalPacket;
        }

        public void Read(Span<byte> buffer, int position = -1)
        {
            int index;
            if (position == -1)
                index = Position;
            else
                index = Math.Clamp(position, 0, _bytes.Length);

            if (index + buffer.Length > _bytes.Length)
                return;

            _bytes.Span.Slice(index, buffer.Length).CopyTo(buffer);

            if (position == -1)
                Position += buffer.Length;
        }

        public T Read<T>(int position = -1) where T : struct
        {
            T result = default;
            Span<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1));
            Read(span, position);
            if (BitConverter.IsLittleEndian && (result is int || result is short || result is long))
                span.Reverse();

            return result;
        }

        public void Write<T>(T value, int position = -1) where T : struct
        {
            Span<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
            if (BitConverter.IsLittleEndian && (value is int || value is short || value is long))
                span.Reverse();
            Write(span, position);
        }

        public void Write(Span<byte> buffer, int position = -1)
        {
            int index;
            if (position == -1)
                index = Position;
            else
                index = Math.Clamp(position, 0, _bytes.Length);

            int endIndex = index + buffer.Length;
            bool overwrites = endIndex > _bytes.Length;
            if (overwrites)
            {
                int newBytesCount = endIndex - _bytes.Length;
                Memory<byte> newBytes = new byte[_bytes.Length + newBytesCount];
                _bytes.CopyTo(newBytes);
                buffer.CopyTo(newBytes.Span.Slice(index, buffer.Length));
                _bytes = newBytes;
                Length += newBytesCount;
            }
            else buffer.CopyTo(_bytes.Span.Slice(index, buffer.Length));

            if (position == -1)
                Position += buffer.Length;
        }

        public string ReadString(int position = -1)
        {
            int index;
            if (position == -1)
                index = Position;
            else
                index = Math.Clamp(position, 0, _bytes.Length);

            short length = Read<short>(position);
            if (index + length + 2 > _bytes.Length)
                return null;

            string result = Encoding.ASCII.GetString(_bytes.Span.Slice(index + 2, length));

            if (position == -1)
                Position += length;

            return result;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            if (Hash != null)
                sb.AppendFormat("[{0}]", Hash);
            sb.AppendFormat("{{l:{0}}}{{h:{1}}}: ", Length, Header);

            int oldPosition = Position;
            Position = 0;
            if (Structure != null)
            {
                for (int i = 0; i < Structure.Length; i++)
                {
                    string format = "{{{0}}}";

                    object result = null;
                    switch (Structure[i])
                    {
                        case PacketValue.Short:
                            result = Read<short>();
                            break;
                        case PacketValue.Int:
                            result = Read<int>();
                            break;
                        case PacketValue.Boolean:
                            result = Read<bool>();
                            break;
                        case PacketValue.String:
                            result = ReadString();
                            break;
                        case PacketValue.Byte:
                            result = Read<byte>();
                            break;
                        case PacketValue.Double:
                            result = Read<double>();
                            break;
                        default:
                            break;
                    }

                    sb.AppendFormat(format, result);
                }
            }
            
            if(Structure == null || (Structure != null && Position != _bytes.Length))
            {
                Span<byte> payloadSpan = _bytes.Span;
                for (int i = Position; i < _bytes.Length; i++)
                {
                    byte value = payloadSpan[i];
                    if (value <= 13)
                        sb.AppendFormat("[{0}]", value);
                    else
                        sb.Append((char)value);
                }
            }

            Position = oldPosition;
            return sb.ToString();
        }
    }
}
