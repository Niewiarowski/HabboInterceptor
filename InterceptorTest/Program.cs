using System;
using System.Threading.Tasks;

using Interceptor;
using Interceptor.Communication;

namespace InterceptorTest
{
    internal class Program
    {
        internal static Task PacketSender;
        internal static async Task Main()
        {
            Console.Title = "InterceptorTest";
            HabboInterceptor interceptor = new HabboInterceptor();
            interceptor.Incoming += packet =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("<- {0}", packet);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("-------------\nPresiona una tecla para enviar un paquete\n-------------");
                return Task.CompletedTask;
            };
            interceptor.Outgoing += packet =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("-> {0}", packet);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("-------------\nPresiona una tecla para enviar un paquete\n-------------");
                return Task.CompletedTask;
            };
            interceptor.Log += message =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(message.ToString());
                return Task.CompletedTask;
            };
            interceptor.Connected += () =>
            {
                PacketSender = StartPacketSender(interceptor);
                return Task.CompletedTask;
            };

            interceptor.Start();
            await Task.Delay(-1);
        }

        internal static Task StartPacketSender(HabboInterceptor interceptor)
        {
            return Task.Factory.StartNew(async () =>
            {
                while (ReadKey())
                {
                    var packet = new Packet(2883);
                    packet.WriteString("Sending packets test");
                    packet.Write(0);
                    packet.Write(0);
                    await interceptor.SendToServerAsync(packet);
                }
            });
        }

        internal static bool ReadKey() => Console.ReadKey() != null;
    }
}
