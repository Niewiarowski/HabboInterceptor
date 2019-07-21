using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

using Interceptor.Habbo;
using Interceptor.Memory;
using Interceptor.Logging;
using Interceptor.Parsing;
using Interceptor.Encryption;
using Interceptor.Interception;
using Interceptor.Communication;

namespace Interceptor
{
    public class HabboInterceptor : Interception.Interceptor
    {
        public delegate Task PacketEvent(Packet packet);
        public delegate Task LogEvent(LogMessage message);
        public PacketEvent Incoming { get; set; }
        public PacketEvent Outgoing { get; set; }
        public HabboPackets Packets { get; } = new HabboPackets();
        public LogEvent Log { get; set; }
        public string Production { get; private set; }
        public bool PauseIncoming { get; set; }
        public bool PauseOutgoing { get; set; }
        public bool InterceptCreatedPackets { get; set; }

        private RC4Key DecipherKey { get; set; }
        private RC4Key CipherKey { get; set; }

        public HabboInterceptor() : base(IPAddress.Loopback, null, 0)
        {
        }

        public override void Start()
        {
            List<Interception.Interceptor> interceptors = new List<Interception.Interceptor>(8);

            Dictionary<string, int> hotels = new Dictionary<string, int>
            {
                {"game-es.habbo.com", 30000 },
                {"game-us.habbo.com", 38101 },
                {"game-nl.habbo.com", 30000 },
                {"game-de.habbo.com", 30000 },
                {"game-br.habbo.com", 30000 },
                {"game-fi.habbo.com", 30000 },
                {"game-it.habbo.com", 30000 },
                {"game-tr.habbo.com", 30000 }
            };

            int localIpCounter = 1;
            foreach ((string host, int port) in hotels)
            {
                string localIp = string.Concat("127.0.0.", localIpCounter++);
                Interception.Interceptor interceptor = new Interception.Interceptor(IPAddress.Parse(localIp), HostHelper.GetIPAddressFromHost(host), port);
                if (!HostHelper.TryAddRedirect(localIp, host))
                {
                    LogInternalAsync(new LogMessage(LogSeverity.Error, "Failed to add host redirect. Run me as an administrator.")).Wait();
                    throw new Exception("Failed to add host redirect.");
                }

                interceptor.Connected += OnConnect;
                interceptor.Start();
                interceptors.Add(interceptor);
            }

            Task OnConnect()
            {
                Interception.Interceptor connectedInterceptor = null;
                foreach (Interception.Interceptor interceptor in interceptors)
                {
                    if (!interceptor.IsConnected)
                        interceptor.Stop();
                    else connectedInterceptor = interceptor;
                }

                if (connectedInterceptor != null)
                {
                    ClientIp = connectedInterceptor.ClientIp;
                    ClientPort = connectedInterceptor.ClientPort;
                    ServerIp = connectedInterceptor.ServerIp;
                    ServerPort = connectedInterceptor.ServerPort;
                }

                Connected += () => LogInternalAsync(!HostHelper.TryRemoveRedirects()
                    ? new LogMessage(LogSeverity.Warning, "Failed to remove host redirect.")
                    : new LogMessage(LogSeverity.Info, "Connected."));

                base.Start();
                return Task.CompletedTask;
            }
        }

        public override void Stop()
        {
            HostHelper.TryRemoveRedirects();
            base.Stop();
        }

        internal async Task LogInternalAsync(LogMessage message)
        {
            if (Log == null) return;

            Delegate[] delegates = Log.GetInvocationList();
            foreach (LogEvent t in delegates)
            {
                try
                {
                    await t(message).ConfigureAwait(false);
                }
                catch { }
            }
        }

        public async Task<T> WaitForAsync<T>(bool outgoing) where T : class
        {
            if(!Packets.TryResolveHeader(typeof(T), out ushort header, outgoing))
                throw new ArgumentException("Type T must have a PacketAttribute attribute.");

            return (await WaitForInternalAsync(null, header, outgoing)).ToObject<T>();
        }

        public Task<Packet> WaitForIncomingAsync(ReadOnlyMemory<char> hash) => WaitForInternalAsync(hash, 0, false);
        public Task<Packet> WaitForOutgoingAsync(ReadOnlyMemory<char> hash) => WaitForInternalAsync(hash, 0, true);
        public Task<Packet> WaitForIncomingAsync(ushort header) => WaitForInternalAsync(null, header, false);
        public Task<Packet> WaitForOutgoingAsync(ushort header) => WaitForInternalAsync(null, header, true);

        internal async Task<Packet> WaitForInternalAsync(ReadOnlyMemory<char> hash, ushort header, bool outgoing)
        {
            TaskCompletionSource<Packet> result = new TaskCompletionSource<Packet>();
            Task attach(Packet packet)
            {
                if ((!hash.IsEmpty && packet.Hash.Span.Equals(hash.Span, StringComparison.OrdinalIgnoreCase)) || (hash.IsEmpty && packet.Header == header))
                    result.SetResult(packet);

                return Task.CompletedTask;
            }

            var @event = outgoing ? Outgoing : Incoming;
            @event += attach;
            Packet packet = await result.Task.ConfigureAwait(false);
            @event -= attach;

            return packet;
        }

        private readonly ConcurrentDictionary<(long CancellationId, Func<Packet, bool> Predicate), PacketEvent> _outgoingFilters
            = new ConcurrentDictionary<(long, Func<Packet, bool>), PacketEvent>();
        public long OutgoingAttach(Func<Packet, bool> predicate, PacketEvent e)
        {
            long id = DateTime.Now.Ticks;

            _outgoingFilters.TryAdd((id, predicate), e);

            if (_outgoingFilters.Count == 1)
                Outgoing += OutgoingFiltering;

            return id;
        }

        public long OutgoingAttach<T>(Func<Packet, bool> predicate, Func<T, Task<bool>> e) where T : class
        {
            StructParser.GetReader(typeof(T));
            StructParser.GetWriter(typeof(T));

            return OutgoingAttach(predicate, async packet =>
            {
                T result = packet.ToObject<T>();
                packet.Blocked = !await e(result);
                packet.FromObject(result);
            });
        }

        public long OutgoingAttach<T>(Func<T, Task<bool>> e) where T : class
        {
            return OutgoingAttach((Packet packet) =>
            {
                Packets.TryResolveHeader(typeof(T), out ushort header, true);
                return packet.Header == header;
            }, e);
        }

        public void OutgoingDetach(long detachId)
        {
            ((long CancellationId, Func<Packet, bool> Predicate) key, PacketEvent _)
                = _outgoingFilters.FirstOrDefault(f => f.Key.CancellationId == detachId);

            if (key.CancellationId != 0)
                _outgoingFilters.Remove(key, out PacketEvent _);

            if (_outgoingFilters.Count == 0)
                Outgoing -= OutgoingFiltering;
        }

        private Task OutgoingFiltering(Packet packet)
        {
            return _outgoingFilters.FirstOrDefault(p => (p.Key.Predicate?.Invoke(packet)).Value).Value?.Invoke(packet) ?? Task.CompletedTask;
        }

        private readonly ConcurrentDictionary<(long CancellationId, Func<Packet, bool> Predicate), PacketEvent> _incomingFilters
            = new ConcurrentDictionary<(long, Func<Packet, bool>), PacketEvent>();
        public long IncomingAttach(Func<Packet, bool> predicate, PacketEvent e)
        {
            long id = DateTime.Now.Ticks;

            _incomingFilters.TryAdd((id, predicate), e);

            if (_incomingFilters.Count == 1)
                Incoming += IncomingFiltering;

            return id;
        }

        public long IncomingAttach<T>(Func<Packet, bool> predicate, Func<T, Task<bool>> e) where T : class
        {
            StructParser.GetReader(typeof(T));
            StructParser.GetWriter(typeof(T));

            return IncomingAttach(predicate, async packet =>
            {
                T result = packet.ToObject<T>();
                packet.Blocked = !await e(result);
                packet.FromObject(result);
            });
        }

        public long IncomingAttach<T>(Func<T, Task<bool>> e) where T : class
        {
            return IncomingAttach((Packet packet) =>
            {
                Packets.TryResolveHeader(typeof(T), out ushort header, false);
                return packet.Header == header;
            }, e);
        }

        public void IncomingDetach(uint detachId)
        {
            ((long CancellationId, Func<Packet, bool> Predicate) key, PacketEvent _)
                = _incomingFilters.FirstOrDefault(f => f.Key.CancellationId == detachId);

            if (key.CancellationId != 0)
                _incomingFilters.Remove(key, out PacketEvent _);

            if (_incomingFilters.Count == 0)
                Incoming -= IncomingFiltering;
        }
        private Task IncomingFiltering(Packet packet)
        {
            return _incomingFilters.FirstOrDefault(p => p.Key.Predicate?.Invoke(packet)
                            ?? false).Value?.Invoke(packet) ?? Task.CompletedTask;
        }

        public Task SendToServerAsync(Packet packet)
        {
            return SendInternalAsync(Server, packet, true);
        }

        public Task SendToServerAsync<T>(T packet) where T : class
        {
            return SendInternalAsync(Server, packet, true);
        }

        public Task SendToClientAsync(Packet packet)
        {
            return SendInternalAsync(Client, packet, true);
        }
        public Task SendToClientAsync<T>(T packet) where T : class
        {
            return SendInternalAsync(Client, packet, true);
        }

        internal Task SendInternalAsync<T>(TcpClient client, T packet, bool created = false) where T : class
        {
            if (!Packets.TryResolveHeader(typeof(T), out ushort header, client == Server))
                throw new ArgumentException("Argument \"packet\" must have a PacketAttribute attribute.");

            Packet resultPacket = new Packet(header);
            resultPacket.FromObject(packet);
            return SendInternalAsync(client, resultPacket, created);
        }

        internal async Task SendInternalAsync(TcpClient client, Packet packet, bool created = false)
        {
            if (!packet.Blocked && packet.Valid)
            {
                bool outgoing = client == Server;
                if (((created && InterceptCreatedPackets) || !created))
                {
                    PacketEvent packetEvent = outgoing ? Outgoing : Incoming;
                    if (packetEvent != null)
                    {
                        foreach (PacketEvent t in packetEvent.GetInvocationList())
                        {
                            try
                            {
                                await t(packet).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
                                await LogInternalAsync(new LogMessage(LogSeverity.Warning, "An exception was thrown in a packet event handler", e));
                            }
                        }
                    }
                }

                if (!packet.Blocked && packet.Valid)
                {
                    Memory<byte> packetBytes = packet.Construct();
                    if (outgoing)
                        CipherKey?.Cipher(packetBytes);
                    await client.GetStream().WriteAsync(packetBytes).ConfigureAwait(false);
                }
            }
        }

        private async Task InterceptKeyAsync()
        {
            await Task.Delay(1000); // Wait for client to finish sending the first 3 packets

            Memory<byte> buffer = new byte[1024];
            int decipherBytesRead = await Client.GetStream().ReadAsync(buffer);

            if (RC4Extractor.TryExtractKey(out RC4Key key))
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Info, $"RC4: {key}"));
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
                                    await LogInternalAsync(new LogMessage(LogSeverity.Info, "Disassembling SWF."));
                                    await Packets.DisassembleAsync(packet.ReadString(4));
                                    disassembledClient = true;
                                }

                                await SendInternalAsync(Server, packet);
                            }

                            return;
                        }
                    }
                }
            }
            else await LogInternalAsync(new LogMessage(LogSeverity.Error, "Could not find RC4 key."));
        }

        private async Task<bool> ReceiveAsync(Stream stream, Memory<byte> buffer)
        {
            int bytesRead = 0;
            do
            {
                int tempBytesRead = await stream.ReadAsync(buffer.Slice(bytesRead));
                if (tempBytesRead == 0)
                    return false;

                bytesRead += tempBytesRead;
            }
            while (bytesRead != buffer.Length);
            return true;
        }

        protected override async Task InterceptAsync(TcpClient client, TcpClient server)
        {
            int outgoingCount = 0;
            bool outgoing = server == Server;
            Memory<byte> buffer = new byte[3072];
            Memory<byte> lengthBuffer = new byte[4];

            NetworkStream clientStream = client.GetStream();
            try
            {
                while (IsConnected)
                {
                    if ((outgoing && PauseOutgoing) || (!outgoing && PauseIncoming))
                    {
                        await Task.Delay(20);
                        continue;
                    }

                    if (outgoing && CipherKey == null)
                    {
                        if (outgoingCount != 5)
                            outgoingCount++;

                        if (outgoingCount == 4)
                            await InterceptKeyAsync();
                    }

                    if (!await ReceiveAsync(clientStream, lengthBuffer))
                        break;

                    if (outgoing)
                        DecipherKey?.Cipher(lengthBuffer);

                    if (BitConverter.IsLittleEndian)
                        lengthBuffer.Span.Reverse();
                    int length = BitConverter.ToInt32(lengthBuffer.Span);

                    Memory<byte> packetBytes = length > buffer.Length ? new byte[length] : buffer.Slice(0, length);
                    if (!await ReceiveAsync(clientStream, packetBytes))
                        break;

                    if (outgoing)
                        DecipherKey?.Cipher(packetBytes);

                    Packet packet = new Packet(length, packetBytes);
                    PacketInformation[] messages = outgoing ? Packets.OutMessages : Packets.InMessages;
                    PacketInformation packetInfo = messages[packet.Header];
                    if (packetInfo.Id != 0)
                    {
                        packet.Hash = packetInfo.Hash;
                        packet.Structure = packetInfo.Structure;
                    }

                    if (outgoing && outgoingCount == 1)
                        Production = packet.ReadString(0);

                    await SendInternalAsync(outgoing ? Server : Client, packet);
                }
            }
            catch (IOException) { }
            catch (Exception e)
            {
                await LogInternalAsync(new LogMessage(LogSeverity.Error, "An exception was thrown during packet interception", e));
            }
            finally
            {
                if (IsConnected)
                {
                    await LogInternalAsync(new LogMessage(LogSeverity.Info, "Disconnected."));
                    Stop();
                }
            }
        }
    }
}