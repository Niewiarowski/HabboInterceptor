using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;

using Interceptor.Communication;
using Interceptor.Encryption;
using Interceptor.Habbo;
using Interceptor.Interception;
using Interceptor.Logging;
using Interceptor.Memory;

namespace Interceptor
{
    public class HabboInterceptor : Interception.Interceptor
    {
        public delegate Task PacketEvent(Packet packet);
        public delegate Task LogEvent(LogMessage message);
        public PacketEvent Incoming { get; set; }
        public PacketEvent Outgoing { get; set; }
        public LogEvent Log { get; set; }
        public Dictionary<ushort, PacketInformation> InMessages { get; } = new Dictionary<ushort, PacketInformation>();
        public Dictionary<ushort, PacketInformation> OutMessages { get; } = new Dictionary<ushort, PacketInformation>();
        public bool Paused { get; set; }
        public string Production { get; private set; }

        private RC4Key DecipherKey { get; set; }
        private RC4Key CipherKey { get; set; }

        public HabboInterceptor() : base(IPAddress.Parse("127.0.0.1"), HostHelper.GetIPAddressFromHost("game-us.habbo.com"), 38101)
        {
        }

        public override void Start()
        {
            if (!HostHelper.TryAddRedirect(ClientIp.ToString(), "game-us.habbo.com"))
            {
                LogInternalAsync(new LogMessage(LogSeverity.Error, "Failed to add host redirect.")).Wait();
                throw new Exception("Failed to add host redirect.");
            }
            else
            {

                Interception.Interceptor interceptor = new Interception.Interceptor(ClientIp, ClientPort, ServerIp, ServerPort);
                interceptor.Start();
                interceptor.Connected += () =>
                {
                    Connected += () =>
                    {
                        if (!HostHelper.TryRemoveRedirects())
                            return LogInternalAsync(new LogMessage(LogSeverity.Warning, "Failed to remove host redirect."));

                        return LogInternalAsync(new LogMessage(LogSeverity.Info, "Connected."));
                    };

                    base.Start();
                    return Task.CompletedTask;
                };
            }
        }

        public override void Stop()
        {
            HostHelper.TryRemoveRedirects();
            base.Stop();
        }

        internal async Task LogInternalAsync(LogMessage message)
        {
            if (Log != null)
            {
                Delegate[] delegates = Log.GetInvocationList();
                for (int i = 0; i < delegates.Length; i++)
                {
                    try
                    {
                        await ((LogEvent)delegates[i])(message).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }

        public Task SendToServerAsync(Packet packet) => SendInternalAsync(Server, packet);
        public Task SendToClientAsync(Packet packet) => SendInternalAsync(Client, packet);

        internal async Task SendInternalAsync(TcpClient client, Packet packet)
        {
            if (!packet.Blocked)
            {
                bool outgoing = client == Server;
                PacketEvent packetEvent = (outgoing ? Outgoing : Incoming);
                if (packetEvent != null)
                {
                    Delegate[] delegates = packetEvent.GetInvocationList();
                    for (int i = 0; i < delegates.Length; i++)
                    {
                        try
                        {
                            await ((PacketEvent)delegates[i])(packet).ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            await LogInternalAsync(new LogMessage(LogSeverity.Warning, "An exception was thrown in a packet event handler", e));
                        }
                    }
                }

                Memory<byte> packetBytes = packet.Construct();
                if (outgoing)
                    CipherKey?.Cipher(packetBytes);
                await client.GetStream().WriteAsync(packetBytes).ConfigureAwait(false);
            }
        }

        private async Task DisassembleAsync(string clientUrl)
        {
            string swfUrl = string.Concat(clientUrl, "Habbo.swf");
            using (WebClient wc = new WebClient())
            await using (Stream stream = await wc.OpenReadTaskAsync(swfUrl))
            using (HGame game = new HGame(stream))
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Info, "Disassembling SWF."));
                game.Disassemble();
                game.GenerateMessageHashes();

                foreach ((ushort id, HMessage message) in game.InMessages)
                {
                    InMessages.Add(id, new PacketInformation(message.Id, message.Hash, message.Structure));
                    message.Class = null;
                    message.Parser = null;
                    message.References.Clear();
                }

                foreach ((ushort id, HMessage message) in game.OutMessages)
                {
                    OutMessages.Add(id, new PacketInformation(message.Id, message.Hash, message.Structure));
                    message.Class = null;
                    message.Parser = null;
                    message.References.Clear();
                }
            }

            GC.Collect();
        }

        private async Task InterceptKeyAsync()
        {
            Paused = true;
            await Task.Delay(1000); // Wait for client to finish sending the first 3 packets

            Memory<byte> buffer = new byte[1024];
            int decipherBytesRead = await Client.GetStream().ReadAsync(buffer);

            if (RC4Extractor.TryExtractKey(out RC4Key key))
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Info, string.Format("RC4: {0}", key)));
                Memory<byte> decipherBuffer = new byte[decipherBytesRead];

                for (int i = 0; i < 256; i++)
                {
                    for (int j = 0; j < 256; j++)
                    {
                        RC4Key tempKey = key.Copy(i, j);
                        tempKey.Reverse(decipherBytesRead);
                        if (tempKey.X == 0 && tempKey.Y == 0)
                        {
                            buffer.Slice(0, decipherBytesRead).CopyTo(decipherBuffer);
                            RC4Key possibleDecipherKey = tempKey.Copy();
                            possibleDecipherKey.Cipher(decipherBuffer);

                            IReadOnlyCollection<Packet> packets = Packet.Parse(decipherBuffer);
                            if (packets.Count == 0)
                                continue;

                            CipherKey = tempKey;
                            DecipherKey = possibleDecipherKey;

                            bool disassembledClient = false;
                            foreach (Packet packet in packets)
                            {
                                if (!disassembledClient)
                                {
                                    await DisassembleAsync(packet.ReadString(4));
                                    disassembledClient = true;
                                }
                                await SendToServerAsync(packet);
                            }

                            goto exit;
                        }
                    }
                }
            }
            else await LogInternalAsync(new LogMessage(LogSeverity.Error, "Could not find RC4 key."));

            exit:
            Paused = false;
        }

        protected override async Task InterceptAsync(TcpClient client, TcpClient server)
        {
            int outgoingCount = 0;
            bool outgoing = server == Server;
            Memory<byte> buffer = new byte[3072];
            Memory<byte> lengthBuffer = new byte[4];

            var clientStream = client.GetStream();
            try
            {
                while (true)
                {
                    if (Paused)
                    {
                        await Task.Delay(10);
                        continue;
                    }

                    if (outgoing && CipherKey == null)
                    {
                        if (outgoingCount != 5)
                            outgoingCount++;

                        if (outgoingCount == 4)
                            await InterceptKeyAsync();
                    }

                    int bytesRead = 0;
                    do bytesRead += await clientStream.ReadAsync(lengthBuffer.Slice(bytesRead));
                    while (bytesRead != lengthBuffer.Length);

                    if (outgoing)
                        DecipherKey?.Cipher(lengthBuffer);

                    if (BitConverter.IsLittleEndian)
                        lengthBuffer.Span.Reverse();
                    int length = BitConverter.ToInt32(lengthBuffer.Span);

                    Memory<byte> packetBytes = length > buffer.Length ? new byte[length] : buffer.Slice(0, length);
                    bytesRead = 0;
                    do bytesRead += await clientStream.ReadAsync(packetBytes.Slice(bytesRead));
                    while (bytesRead < length);

                    if (outgoing)
                        DecipherKey?.Cipher(packetBytes);

                    Packet packet = new Packet(length, packetBytes);
                    Dictionary<ushort, PacketInformation> messages = outgoing ? OutMessages : InMessages;
                    if (messages.TryGetValue(packet.Header, out PacketInformation packetInfo))
                    {
                        packet.Hash = packetInfo.Hash;
                        packet.Structure = packetInfo.Structure;
                    }

                    if (outgoing)
                    {
                        if (outgoingCount == 1)
                            Production = packet.ReadString(0);

                        await SendToServerAsync(packet);
                    }
                    else
                        await SendToClientAsync(packet);
                }
            }
            catch (Exception e)
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Error, "An exception was thrown during packet interception", e));
            }
        }
    }
}