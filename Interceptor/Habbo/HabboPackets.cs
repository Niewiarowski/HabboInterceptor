using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;

using Flazzy.ABC;
using Flazzy.Tags;

using Interceptor.Attributes;
using System.Runtime.InteropServices;

namespace Interceptor.Habbo
{
    public class HabboPackets
    {
        public delegate Task DisassembleCompletedEvent();
        public DisassembleCompletedEvent DisassembleCompleted { get; set; }
        public PacketInformation[] InMessages { get; } = new PacketInformation[4001];
        public PacketInformation[] OutMessages { get; } = new PacketInformation[4001];

        private Dictionary<Type, ushort> _classHeaders { get; } = new Dictionary<Type, ushort>();

        private string ClientUrl { get; set; }

        public PacketInformation GetPacketInformation(ulong hash, bool outgoing)
        {
            PacketInformation[] packets = outgoing ? OutMessages : InMessages;
            for (int i = 0; i < packets.Length; i++)
                if (packets[i].Hash == hash)
                    return packets[i];

            return default;
        }

        public PacketInformation GetPacketInformation(ushort id, bool outgoing)
        {
            PacketInformation[] packets = outgoing ? OutMessages : InMessages;
            for (int i = 0; i < packets.Length; i++)
                if (packets[i].Id == id)
                    return packets[i];

            return default;
        }

        public bool TryResolveHeader(Type type, out ushort header, bool outgoing)
        {
            if (ClientUrl == null)
            {
                header = ushort.MaxValue;
                return false;
            }

            if (!_classHeaders.TryGetValue(type, out header))
            {
                object[] attributes = type.GetCustomAttributes(false);
                PacketAttribute packetAttribute = null;
                for (int i = 0; i < attributes.Length; i++)
                {
                    if (attributes[i] is PacketAttribute match)
                    {
                        packetAttribute = match;
                        break;
                    }
                }

                if (packetAttribute == null)
                    header = ushort.MaxValue;
                else
                {
                    if (packetAttribute.Hash == 0)
                        header = packetAttribute.Header;
                    else
                        header = GetPacketInformation(packetAttribute.Hash, outgoing).Id;
                }

                _classHeaders.Add(type, header);
            }

            return header != ushort.MaxValue;
        }

        public async Task DisassembleAsync(string clientUrl, bool cacheClient)
        {
            if (ClientUrl != null && ClientUrl == clientUrl)
                return;

            ClientUrl = clientUrl;

            Stream stream = null;
            string filePath = null;
            bool createCache = false;
            using var wc = new WebClient();
            string swfUrl = string.Concat(ClientUrl, "Habbo.swf");

            if (!cacheClient)
            {
                stream = await wc.OpenReadTaskAsync(swfUrl).ConfigureAwait(false);
            }
            else
            {
                var version = ClientUrl.Split("/")[4];
                filePath = string.Concat(Path.GetTempPath(), $"{version}.swfd");

                if (!File.Exists(filePath))
                {
                    stream = await wc.OpenReadTaskAsync(swfUrl).ConfigureAwait(false);
                    createCache = true;
                }
                else
                {
                    ReadCache(filePath);
                    DisassembleCompleted?.Invoke();
                    return;
                }
            }

            using HGame game = new HGame(stream);

            game.Disassemble();
            game.GenerateMessageHashes();

            foreach ((ushort id, HMessage message) in game.InMessages)
            {
                InMessages[id] = new PacketInformation(message.Id, message.Hash, message.Structure);
                message.Class = null;
                message.Parser = null;
                message.References.Clear();
            }

            foreach ((ushort id, HMessage message) in game.OutMessages)
            {
                OutMessages[id] = new PacketInformation(message.Id, message.Hash, message.Structure);
                message.Class = null;
                message.Parser = null;
                message.References.Clear();
            }

            DisassembleCompleted?.Invoke();

            foreach (ABCFile abc in game.ABCFiles)
            {
                ((Dictionary<ASMultiname, List<ASClass>>)abc.GetType().GetField("_classesCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(abc)).Clear();

                abc.Methods.Clear();
                abc.Metadata.Clear();
                abc.Instances.Clear();
                abc.Classes.Clear();
                abc.Scripts.Clear();
                abc.MethodBodies.Clear();

                abc.Pool.Integers.Clear();
                abc.Pool.UIntegers.Clear();
                abc.Pool.Doubles.Clear();
                abc.Pool.Strings.Clear();
                abc.Pool.Namespaces.Clear();
                abc.Pool.NamespaceSets.Clear();
                abc.Pool.Multinames.Clear();

                abc.Dispose();
            }

            game.Tags.Clear();
            ((Dictionary<ASClass, HMessage>)typeof(HGame).GetField("_messages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(game)).Clear();
            ((Dictionary<DoABCTag, ABCFile>)typeof(HGame).GetField("_abcFileTags", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(game)).Clear();
            game.ABCFiles.Clear();

            if (createCache)
                CreateCache(filePath);

            GC.Collect();
        }

        private void CreateCache(string filePath)
        {
            using Stream cacheStream = File.OpenWrite(filePath);
            Span<byte> infoSpan = stackalloc byte[14];
            Span<ushort> headerSpan = MemoryMarshal.Cast<byte, ushort>(infoSpan);
            Span<ulong> hashSpan = MemoryMarshal.Cast<byte, ulong>(infoSpan.Slice(2));
            Span<int> structureLengthSpan = MemoryMarshal.Cast<byte, int>(infoSpan.Slice(10));

            for (int i = 0; i < 8000; i++)
            {
                PacketInformation packetInfo = i > 4000 ? OutMessages[i % 4001] : InMessages[i];
                headerSpan[0] = packetInfo.Id;
                hashSpan[0] = packetInfo.Hash;

                if (packetInfo.Structure != null)
                {
                    structureLengthSpan[0] = packetInfo.Structure.Length;
                    cacheStream.Write(infoSpan);

                    for (int h = 0; h < packetInfo.Structure.Length; h++)
                        cacheStream.WriteByte((byte)packetInfo.Structure[h]);
                }
                else
                {
                    structureLengthSpan[0] = 0;
                    cacheStream.Write(infoSpan);
                }
            }
        }

        private void ReadCache(string filePath)
        {
            using Stream stream = File.OpenRead(filePath);
            Span<byte> infoSpan = stackalloc byte[14];
            Span<ushort> headerSpan = MemoryMarshal.Cast<byte, ushort>(infoSpan);
            Span<ulong> hashSpan = MemoryMarshal.Cast<byte, ulong>(infoSpan.Slice(2));
            Span<int> structureLengthSpan = MemoryMarshal.Cast<byte, int>(infoSpan.Slice(10));

            for (int i = 0; i < 8000; i++)
            {
                PacketValue[] structure = null;

                stream.Read(infoSpan);

                int structureLength = structureLengthSpan[0];
                if(structureLength > 0)
                {
                    structure = new PacketValue[structureLength];
                    for (int h = 0; h < structureLength; h++)
                        structure[h] = (PacketValue)stream.ReadByte();
                }

                bool outgoing = i > 4000;
                PacketInformation[] messages = outgoing ? OutMessages : InMessages;
                messages[outgoing ? i % 4001 : i] = new PacketInformation(headerSpan[0], hashSpan[0], structure);
            }
        }
    }
}
