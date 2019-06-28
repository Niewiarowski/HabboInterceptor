using System;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

using Interceptor.Communication;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Interceptor.Parsing
{
    public class WriterContext
    {
        public Packet Packet { get; }
        public object Value { get; }

        public WriterContext(Packet packet, object value)
        {
            Packet = packet;
            Value = value;
        }
    }

    internal static class StructParser
    {
        private static Dictionary<Type, ScriptRunner<object>> Readers { get; } = new Dictionary<Type, ScriptRunner<object>>();
        private static Dictionary<Type, ScriptRunner<object>> Writers { get; } = new Dictionary<Type, ScriptRunner<object>>();

        public static T Read<T>(Packet packet)
        {
            return (T)Read(packet, typeof(T));
        }

        public static object Read(Packet packet, Type type)
        {
            return GetReader(type).Invoke(packet).Result;
        }

        public static void Write<T>(Packet packet, T value)
        {
            Write(packet, typeof(T), value);
        }

        public static void Write(Packet packet, Type type, object value)
        {
            GetWriter(type).Invoke(new WriterContext(packet, value)).Wait();
        }

        internal static ScriptRunner<object> GetReader(Type type)
        {
            if (!Readers.TryGetValue(type, out ScriptRunner<object> reader))
            {
                reader = CreateParser(type, true);
                GC.Collect();
            }

            return reader;
        }

        internal static ScriptRunner<object> GetWriter(Type type)
        {
            if (!Writers.TryGetValue(type, out ScriptRunner<object> writer))
            {
                writer = CreateParser(type, false);
                GC.Collect();
            }

            return writer;
        }

        private static ScriptRunner<object> CreateParser(Type type, bool reader)
        {
            string sourceCode = reader ? GenerateReaderCode(type) : GenerateWriterCode(type);
            var options = ScriptOptions.Default
                .WithReferences(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location)));

            Script<object> script = CSharpScript.Create(sourceCode, options, reader ? typeof(Packet) : typeof(WriterContext));
            var errors = script.Compile();
            if (errors.Any(xd => xd.Severity == DiagnosticSeverity.Error))
                throw new Exception(errors[0].ToString());

            ScriptRunner<object> scriptRunner = script.CreateDelegate();
            (reader ? Readers : Writers).Add(type, scriptRunner);
            return scriptRunner;
        }

        private static string GenerateReaderCode(Type type)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("var result = new {0}();\n", type.GetCleanFullName());

            foreach (PropertyInfo property in type.GetProperties())
            {
                Type propType = property.PropertyType;

                string generateRead(Type type)
                {
                    if (propType.IsClass)
                        return string.Format("ToObject<{0}>(false);\n", type.GetCleanFullName());
                    else
                        return string.Format("Read<{0}>();\n", type.GetCleanFullName());
                }

                if (propType == typeof(string))
                    sb.AppendFormat("result.{0} = ReadString();\n", property.Name);
                else if (propType.IsArray)
                {
                    Type arrayType = propType.GetElementType();
                    sb.AppendFormat("result.{0} = new {1}[Read<int>()];\nfor(int i = 0; i < result.{0}.Length; i++)\n{{\nresult.{0}[i] = {2}}}\n", property.Name, arrayType.GetCleanFullName(), generateRead(arrayType));

                }
                else
                    sb.AppendFormat("result.{0} = {1}", property.Name, generateRead(propType));
            }

            sb.Append("return result;");
            return sb.ToString();
        }

        private static string GenerateWriterCode(Type type)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("var value = ({0})Value;\n", type.GetCleanFullName());

            foreach (PropertyInfo property in type.GetProperties())
            {
                Type propType = property.PropertyType;

                string generateWrite(string value, Type type)
                {
                    if (propType.IsClass)
                        return string.Format("Packet.FromObject<{0}>({1}, false);\n", type.GetCleanFullName(), value);
                    else
                        return string.Format("Packet.Write<{0}>({1});\n", type.GetCleanFullName(), value);
                }

                if (propType == typeof(string))
                    sb.AppendFormat("Packet.WriteString(value.{0});\n", property.Name);
                else if (propType.IsArray)
                {
                    Type arrayType = propType.GetElementType();
                    //sb.AppendFormat("Packet.Write<int>(value.{0}.Length);\nfor(int i = 0; i < value.{0}.Length; i++)\n{{\nPacket.{2}<{1}>(value.{0}[i]);}}\n", propType.Name, arrayType.GetCleanFullName());
                    sb.AppendFormat("Packet.Write<int>(value.{0}.Length);\nfor(int i = 0; i < value.{0}.Length; i++)\n{{\n{2}}}\n", property.Name, arrayType.GetCleanFullName(), generateWrite(string.Format("value.{0}[i]", property.Name), arrayType));
                }
                else
                    //sb.AppendFormat("Packet.{2}<{0}>(value.{1}, {3});\n", propType.GetCleanFullName(), property.Name, propType.IsClass ? "FromObject" : "Write", propType.IsClass ? "false" : "true");
                    sb.Append(generateWrite(string.Concat("value.", property.Name), propType));
            }

            return sb.ToString();
        }

        private static string GetCleanFullName(this Type type)
        {
            if (type.IsGenericType)
            {
                return string.Format(
                    "{0}<{1}>",
                    type.FullName.Substring(0, type.FullName.LastIndexOf("`", StringComparison.InvariantCulture)),
                    string.Join(", ", type.GetGenericArguments().Select(GetCleanFullName)));
            }

            return type.FullName.Replace('+', '.');
        }
    }
}
