using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Interceptor.Interception
{
    public class Interceptor
    {
        public IPAddress ClientIp { get; protected set; }
        public int ClientPort { get; protected set; }
        public IPAddress ServerIp { get; protected set; }
        public int ServerPort { get; protected set; }
        public Func<Task> Connected { get; set; }
        public Func<Task> Disconnnected { get; set; }
        public bool IsConnected { get; protected set; }

        protected TcpClient Client { get; set; }
        protected TcpClient Server { get; private set; }

        private Task ConnectionTask { get; set; }
        private Task ClientTask { get; set; }
        private Task ServerTask { get; set; }
        private bool HasStarted { get; set; }
        private object DisconnectLock { get; } = new object();

        public Interceptor(IPAddress clientIp, IPAddress serverIp, int port) : this(clientIp, port, serverIp, port) { }
        public Interceptor(IPAddress clientIp, int clientPort, IPAddress serverIp, int serverPort)
        {
            ClientIp = clientIp;
            ClientPort = clientPort;
            ServerIp = serverIp;
            ServerPort = serverPort;
        }

        public virtual void Start()
        {
            if (!HasStarted)
            {
                HasStarted = true;
                ConnectionTask = Task.Factory.StartNew(ConnectAsync);
            }
        }

        public virtual void Stop()
        {
            lock (DisconnectLock)
            {
                if (!IsConnected)
                    return;

                Client.GetStream().Close();
                Server.GetStream().Close();
                Client.Close();
                Server.Close();

                if (IsConnected)
                {
                    if (Disconnnected != null)
                    {
                        Delegate[] delegates = Disconnnected.GetInvocationList();
                        for (int i = 0; i < delegates.Length; i++)
                            try
                            {
                                _ = ((Func<Task>)delegates[i])();
                            }
                            catch { }
                    }
                }

                IsConnected = false;
                HasStarted = false;
            }
        }

        private async Task ConnectAsync()
        {
            TcpListener listener = new TcpListener(ClientIp, ClientPort);
            listener.Start();
            Client = await listener.AcceptTcpClientAsync();
            Client.NoDelay = true;
            Server = new TcpClient
            {
                NoDelay = true
            };
            await Server.ConnectAsync(ServerIp, ServerPort);
            listener.Stop();
            IsConnected = true;

            if (Connected != null)
            {
                Delegate[] delegates = Connected.GetInvocationList();
                for (int i = 0; i < delegates.Length; i++)
                {
                    try
                    {
                        await ((Func<Task>)delegates[i])().ConfigureAwait(false);
                    }
                    catch { }
                }
            }

            ClientTask = Task.Factory.StartNew(() => InterceptAsync(Client, Server), TaskCreationOptions.LongRunning);
            ServerTask = Task.Factory.StartNew(() => InterceptAsync(Server, Client), TaskCreationOptions.LongRunning);
        }

        protected virtual async Task InterceptAsync(TcpClient client, TcpClient server)
        {
            Memory<byte> buffer = new byte[1024];
            while (client.Connected && server.Connected)
            {
                int bytesRead = await client.GetStream().ReadAsync(buffer);
                if (bytesRead <= 0)
                    return;

                await server.GetStream().WriteAsync(buffer.Slice(0, bytesRead));
            }
        }
    }
}
