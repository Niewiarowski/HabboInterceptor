using Flazzy.ABC;
using Flazzy.Tags;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Interceptor.Habbo
{
    public class HabboPackets
    {
        public PacketInformation[] InMessages { get; } = new PacketInformation[4001];
        public PacketInformation[] OutMessages { get; } = new PacketInformation[4001];

        private bool _hasDisassembled { get; set; }

        public PacketInformation GetPacketInformation(ReadOnlySpan<char> hash, bool outgoing)
        {
            PacketInformation[] packets = outgoing ? OutMessages : InMessages;
            for (int i = 0; i < packets.Length; i++)
                if (packets[i].Hash.Span.Equals(hash, StringComparison.Ordinal))
                    return packets[i];

            return default;
        }

        public async Task DisassembleAsync(string clientUrl)
        {
            if (_hasDisassembled)
                return;

            _hasDisassembled = true;

            string swfUrl = string.Concat(clientUrl, "Habbo.swf");
            using (WebClient wc = new WebClient())
            await using (Stream stream = await wc.OpenReadTaskAsync(swfUrl))
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

                foreach (var abc in game.ABCFiles)
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
