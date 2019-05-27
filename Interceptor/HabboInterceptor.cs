using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

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
        public Dictionary<ushort, PacketInformation> InMessages { get; private set; } = new Dictionary<ushort, PacketInformation>();
        public Dictionary<ushort, PacketInformation> OutMessages { get; private set; } = new Dictionary<ushort, PacketInformation>();
        public bool Paused { get; set; }

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
                        if (!HostHelper.TryRemoveRedirect(ClientIp.ToString(), "game-us.habbo.com"))
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
            HostHelper.TryRemoveRedirect(ClientIp.ToString(), "game-us.habbo.com");
            base.Stop();
        }

        internal async Task LogInternalAsync(LogMessage message)
        {
            try
            {
                if (Log != null)
                    await Log.Invoke(message).ConfigureAwait(false);
            }
            catch { }
        }

        public Task SendToServerAsync(Packet packet) => SendInternalAsync(Server, packet);
        public Task SendToClientAsync(Packet packet) => SendInternalAsync(Client, packet);

        internal async Task SendInternalAsync(TcpClient client, Packet packet)
        {
            if (!packet.Blocked)
            {
                bool outgoing = client == Server;
                try
                {
                    Task invokeTask = (outgoing ? Outgoing : Incoming)?.Invoke(packet);
                    if (invokeTask != null)
                        await invokeTask.ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    await LogInternalAsync(new LogMessage(LogSeverity.Warning, "An exception was thrown in a packet event handler", e));
                }

                Memory<byte> packetBytes = packet.Construct();
                if (outgoing)
                    CipherKey?.Cipher(packetBytes);
                await client.GetStream().WriteAsync(packetBytes).ConfigureAwait(false);
            }
        }

        private async Task DisassembleAsync(string production)
        {
            string swfUrl = string.Format("http://images.habbo.com/gordon/{0}/Habbo.swf", production);
            using (WebClient wc = new WebClient())
            using (Stream stream = await wc.OpenReadTaskAsync(swfUrl))
            using (HGame game = new HGame(stream))
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Info, "Disassembling SWF."));
                game.Disassemble();
                game.GenerateMessageHashes();

                foreach ((ushort id, MessageItem message) in game.InMessages)
                    InMessages.Add(id, new PacketInformation(message.Id, message.Hash, message.Structure));
                foreach ((ushort id, MessageItem message) in game.OutMessages)
                    OutMessages.Add(id, new PacketInformation(message.Id, message.Hash, message.Structure));
            }

            GC.Collect();
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
                        Thread.Sleep(10);
                        continue;
                    }

                    if (outgoing && CipherKey == null)
                    {
                        if (outgoingCount != 5)
                            outgoingCount++;

                        if (outgoingCount == 4)
                        {
                            Paused = true;
                            await Task.Delay(1000); // Wait for client to finish sending the first 3 packets
                            int decipherBytesRead = await clientStream.ReadAsync(buffer);

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

                                            Packet[] packets = Packet.Parse(decipherBuffer);
                                            if (packets.Length == 0)
                                                continue;

                                            CipherKey = tempKey;
                                            DecipherKey = possibleDecipherKey;

                                            for (int x = 0; x < packets.Length; x++)
                                                await SendToServerAsync(packets[x]);

                                            goto exit;
                                        }
                                    }
                                }
                            }
                            else await LogInternalAsync(new LogMessage(LogSeverity.Error, "Could not find RC4 key."));

                            exit:
                            Paused = false;
                        }
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
                    if (messages != null && messages.TryGetValue(packet.Header, out PacketInformation packetInfo))
                    {
                        packet.Hash = packetInfo.Hash;
                        packet.Structure = packetInfo.Structure;
                    }

                    if (outgoing)
                    {
                        if (outgoingCount == 1)
                        {
                            await DisassembleAsync(packet.ReadString());
                            packet.Position = 0;
                        }

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