using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;

using Flazzy.ABC;
using Flazzy.Tags;
using Interceptor.Attributes;

namespace Interceptor.Habbo
{
    public class HabboPackets
    {
        public PacketInformation[] InMessages { get; } = new PacketInformation[4001];
        public PacketInformation[] OutMessages { get; } = new PacketInformation[4001];
        private Dictionary<Type, ushort> _classHeaders { get; } = new Dictionary<Type, ushort>();

        private bool HasDisassembled { get; set; }

        public PacketInformation GetPacketInformation(ReadOnlySpan<char> hash, bool outgoing)
        {
            PacketInformation[] packets = outgoing ? OutMessages : InMessages;
            for (int i = 0; i < packets.Length; i++)
                if (packets[i].Hash.Span.Equals(hash, StringComparison.Ordinal))
                    return packets[i];

            return default;
        }

        public bool TryResolveHeader(Type type, out ushort header, bool outgoing)
        {
            if (!HasDisassembled)
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
                    if (packetAttribute.Hash.IsEmpty)
                        header = packetAttribute.Header;
                    else
                        header = GetPacketInformation(packetAttribute.Hash.Span, outgoing).Id;
                }

                _classHeaders.Add(type, header);
            }

            return header != ushort.MaxValue;
        }

        public async Task DisassembleAsync(string clientUrl)
        {
            if (HasDisassembled)
                return;

            HasDisassembled = true;

            using WebClient wc = new WebClient();
            string swfUrl = string.Concat(clientUrl, "Habbo.swf");
            await using Stream stream = await wc.OpenReadTaskAsync(swfUrl);
            using (HGame game = new HGame(stream))
            {
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
            }

            GC.Collect();
        }
    }
}
