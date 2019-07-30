using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Interceptor.Habbo;
using Interceptor.Parsing;

namespace Interceptor.Communication
{
    public class Packet
    {
        public int Length { get; private set; }
        internal int ConstructLength => Length + 6;
        public ushort Header { get; internal set; }
        public ReadOnlyMemory<byte> Bytes => _bytes;
        public bool Blocked { get; set; }
        public bool Valid => Length == _bytes.Length;
        public ReadOnlyMemory<char> Hash { get; internal set; }
        public PacketValue[] Structure { get; internal set; }

        private int _position;
        public int Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _bytes.Length);
        }

        private Memory<byte> _bytes { get; set; }

        internal Packet(Memory<byte> bytes) : this(bytes.Span) { }
        internal Packet(Span<byte> bytes) : this(bytes, out _, 0) { }
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

        public static Packet FromBytes(int packetLength, Memory<byte> bytes) => FromBytes(packetLength, bytes.Span);
        public static Packet FromBytes(int packetLength, Span<byte> bytes) => new Packet(packetLength, bytes);

        internal Packet(int length, Memory<byte> bytes) : this(length, bytes.Span) { }
        internal Packet(int length, Span<byte> bytes)
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

        internal Packet(int length, ushort header, Span<byte> bytes)
        {
            Length = length - 2;
            Header = header;
            _bytes = bytes.ToArray();
        }

        public Packet(ReadOnlyMemory<char> hash)
        {
            Hash = hash;
        }

        public Packet(ReadOnlyMemory<char> hash, int length) : this(hash)
        {
            _bytes = new byte[length];
            Length = length;
        }

        public Packet(ushort header)
        {
            Header = header;
        }

        public Packet(ushort header, int length) : this(header)
        {
            _bytes = new byte[length];
            Length = length;
        }

        public static IReadOnlyCollection<Packet> Parse(Memory<byte> bytes) => Parse(bytes.Span);
        public static IReadOnlyCollection<Packet> Parse(Span<byte> bytes)
        {
            List<Packet> result = new List<Packet>();
            int remainderIndex = 0;
            while (remainderIndex >= 0)
                result.Add(new Packet(bytes, out remainderIndex, remainderIndex));

            if (remainderIndex == -2) return Array.Empty<Packet>();
            return result.AsReadOnly();
        }

        internal void ConstructTo(Span<byte> finalPacket)
        {
            Span<byte> lengthSlice = finalPacket.Slice(0, 4);
            Span<byte> headerSlice = finalPacket.Slice(4, 2);
            Span<byte> payloadSlice = finalPacket.Slice(6);
            BitConverter.TryWriteBytes(lengthSlice, Length + 2);
            BitConverter.TryWriteBytes(headerSlice, Header);
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

        private int GetSafeIndex(int position) => position == -1 ? Position : Math.Clamp(position, 0, _bytes.Length);

        public void Read(Span<byte> buffer, int position = -1)
        {
            int index = GetSafeIndex(position);

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

        public T ToObject<T>(bool restorePosition = true) where T : class
        {
            int oldPosition = 0;
            T result = StructParser.Read<T>(this);

            if (restorePosition)
                Position = oldPosition;

            return result;
        }

        public string ReadString(int position = -1)
        {
            int index = GetSafeIndex(position);

            short length = Read<short>(position);
            if (index + length + 2 > _bytes.Length)
                return null;

            string result = Encoding.UTF8.GetString(_bytes.Span.Slice(index + 2, length));

            if (position == -1)
                Position += length;

            return result;
        }

        public void Write(Span<byte> buffer, int position = -1)
        {
            int index = GetSafeIndex(position);
            int endIndex = index + buffer.Length;
            bool overwrites = endIndex > _bytes.Length;
            if (overwrites)
                Resize(endIndex);

            buffer.CopyTo(_bytes.Span.Slice(index, buffer.Length));

            if (position == -1)
                Position += buffer.Length;
        }

        public void Write<T>(T value, int position = -1) where T : struct
        {
            Type type = typeof(T);
            if (type.IsValueType && !type.IsEnum && !type.IsPrimitive)
            {
                StructParser.Write(this, value);
                if (Position != Length)
                    Resize(Position);
            }
            else
            {
                Span<byte> span = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
                if (BitConverter.IsLittleEndian && (value is int || value is short || value is long))
                    span.Reverse();
                Write(span, position);
            }
        }
        public void FromObject<T>(T value, bool restorePosition = true) where T : class
        {
            int oldPosition = 0;
            StructParser.Write<T>(this, value);

            if (restorePosition)
                Position = oldPosition;
        }

        public void WriteString(ReadOnlySpan<char> buffer, int position = -1)
        {
            int index = GetSafeIndex(position);

            Span<byte> bufferBytes = stackalloc byte[Encoding.UTF8.GetByteCount(buffer)];
            Encoding.UTF8.GetBytes(buffer, bufferBytes);
            Write((short)bufferBytes.Length, index);
            Write(bufferBytes, index + 2);

            if (position == -1)
                Position += bufferBytes.Length + 2;
        }

        public void ReplaceString(ReadOnlySpan<char> buffer, int position = -1)
        {
            int index = GetSafeIndex(position);

            Resize(index + 2, Read<short>(index), buffer.Length);
            WriteString(buffer, index);

            if (position == -1)
                Position += buffer.Length + 2;
        }

        private void Resize(int newLength) => Resize(_bytes.Length, newLength - _bytes.Length);
        private void Resize(int index, int oldLength, int newLength)
        {
            if (oldLength != newLength)
            {
                int resizeIndex = index + oldLength;
                Resize(resizeIndex, newLength - oldLength);
            }
        }
        private void Resize(int index, int count)
        {
            if (count != 0)
            {
                Memory<byte> newBytes = new byte[_bytes.Length + count];
                _bytes.Slice(0, count < 0 ? index + count : index).CopyTo(newBytes);
                _bytes.Slice(index).CopyTo(newBytes.Slice(index + count));
                _bytes = newBytes;
                Length += count;
            }
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            if (!Hash.IsEmpty)
                sb.Append('[').Append(Hash).Append(']');
            sb.Append("{l:").Append(Length).Append("}{h:").Append(Header).Append("}: ");

            int oldPosition = Position;
            Position = 0;
            if (Structure != null)
            {
                for (int i = 0; i < Structure.Length; i++)
                {
                    sb.Append('{');
                    switch (Structure[i])
                    {
                        case PacketValue.Short:
                            sb.Append(Read<short>());
                            sb.Append('s');
                            break;
                        case PacketValue.Integer:
                            sb.Append(Read<int>());
                            sb.Append('i');
                            break;
                        case PacketValue.Boolean:
                            sb.Append(Read<bool>());
                            break;
                        case PacketValue.String:
                            sb.Append(ReadString());
                            break;
                        case PacketValue.Byte:
                            sb.Append(Read<byte>());
                            sb.Append('b');
                            break;
                        case PacketValue.Double:
                            sb.Append(Read<double>());
                            sb.Append('d');
                            break;
                    }

                    sb.Append('}');
                }
            }

            if (Structure == null || (Structure != null && Position != _bytes.Length))
            {
                Span<byte> payloadSpan = _bytes.Span;
                for (int i = Position; i < _bytes.Length; i++)
                {
                    byte value = payloadSpan[i];
                    if (value <= 13)
                        sb.Append('[').Append(value).Append(']');
                    else
                        sb.Append((char)value);
                }
            }

            Position = oldPosition;
            return sb.ToString();
        }
    }
}
