using System;
using System.Text;

namespace Interceptor.Encryption
{
    public class RC4Key
    {
        public ReadOnlyMemory<byte> Bytes => _key;
        public int X { get; private set; }
        public int Y { get; private set; }

        private Memory<byte> _key { get; set; }

        public RC4Key(ReadOnlySpan<byte> buffer)
        {
            _key = new byte[256];
            Span<byte> key = _key.Span;

            for (int i = 0; i < 256; i++)
                key[i] = (byte)i;

            for (int x = 0, y = 0; y < key.Length; y++)
            {
                x += key[y];
                x += buffer[y % buffer.Length];
                x %= key.Length;
                (key[x], key[y]) = (key[y], key[x]);
            }
        }

        public RC4Key Copy() => Copy(X, Y);
        public RC4Key Copy(int x, int y) => Copy(_key.Span, x, y);
        public static RC4Key Copy(ReadOnlySpan<byte> key, int x = 0, int y = 0) =>
            new RC4Key
            {
                _key = key.ToArray(),
                X = x,
                Y = y
            };

        public void Reverse(int count)
        {
            Span<byte> key = _key.Span;
            for (int i = 0; i < count; i++)
            {
                (key[X], key[Y]) = (key[Y], key[X]);
                Y -= key[X];
                Y &= 0xff;
                X--;
                X &= 0xff;
            }
        }

        public void Cipher(Memory<byte> buffer) => Cipher(buffer.Span);
        public void Cipher(Span<byte> buffer)
        {
            Span<byte> key = _key.Span;
            for (int i = 0; i < buffer.Length; i++)
            {
                X++;
                X %= 256;
                Y += key[X];
                Y %= 256;
                (key[X], key[Y]) = (key[Y], key[X]);
                buffer[i] = (byte)(buffer[i] ^ key[((key[X]) + (key[Y])) % 256]);
            }
        }

        public override string ToString()
        {
            Span<byte> keySpan = _key.Span;
            StringBuilder sb = new StringBuilder(_key.Length * 2);
            for (int i = 0; i < _key.Length; i++)
                sb.AppendFormat("{0:X2}", keySpan[i]);

            return sb.ToString();
        }
    }
}
