using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Interceptor.Interception
{
    public class Interceptor
    {
        public IPAddress ClientIp { get; }
        public int ClientPort { get; }
        public IPAddress ServerIp { get; }
        public int ServerPort { get; }

        public Func<Task> Connected;

        protected TcpClient Client { get; private set; }
        protected TcpClient Server { get; private set; }

        private Task ConnectionTask { get; set; }
        private Task ClientTask { get; set; }
        private Task ServerTask { get; set; }
        private bool HasStarted { get; set; }

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
            Client.GetStream().Close();
            Server.GetStream().Close();
            Client.Close();
            Server.Close();
        }

        private async Task ConnectAsync()
        {
            TcpListener listener = new TcpListener(ClientIp, ClientPort);
            listener.Start();
            Client = await listener.AcceptTcpClientAsync();
            Client.NoDelay = true;
            Server = new TcpClient()
            {
                NoDelay = true
            };
            await Server.ConnectAsync(ServerIp, ServerPort);
            listener.Stop();
            Connected?.Invoke();

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
