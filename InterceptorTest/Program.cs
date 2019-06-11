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
                Console.WriteLine("-------------\nPress a key to send a packet\n-------------");
                return Task.CompletedTask;
            };
            interceptor.Outgoing += packet =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("-> {0}", packet);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("-------------\nPress a key to send a packet\n-------------");
                return Task.CompletedTask;
            };
            interceptor.Log += message =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(message.ToString());
                return Task.CompletedTask;
            };

            //NEW
            interceptor.OutgoingAttach(p => p.Hash.ToString() == "3ee5fd", async packet => //returns uint detachId
            {
                string action = string.Empty;
                for (int i = 0; i <= 2; i++)
                    action = packet.ReadString();

                var p = new Packet(2883);
                p.WriteString(action);
                p.Write(0);
                p.Write(0);
                await interceptor.SendToServerAsync(p);
            });

            interceptor.Start();
            await Task.Delay(-1);
        }

        internal static bool ReadKey() => Console.ReadKey() != null;
    }
}
