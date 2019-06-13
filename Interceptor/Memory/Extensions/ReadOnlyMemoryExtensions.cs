using System;

namespace Interceptor.Memory.Extensions
{
    public static class ReadOnlyMemoryExtensions
    {
        public static bool EqualsString(this ReadOnlyMemory<char> memory, string source) 
            => memory.Span.Equals(source, StringComparison.Ordinal);
    }
}
