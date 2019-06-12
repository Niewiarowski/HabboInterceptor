using System;
using System.Threading.Tasks;

using Interceptor;
using Interceptor.Communication;

namespace InterceptorTest
{
    internal class Program
    {
        internal static async Task Main()
        {
            Console.Title = "InterceptorTest";
            HabboInterceptor interceptor = new HabboInterceptor();
            interceptor.Incoming += packet =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("<- {0}", packet);
                return Task.CompletedTask;
            };
            interceptor.Outgoing += packet =>
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("-> {0}", packet);
                return Task.CompletedTask;
            };
            interceptor.Log += message =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(message.ToString());
                return Task.CompletedTask;
            };

            //NEW
            interceptor.OutgoingAttach(p => p.Hash.Equals("1d79f1"), async packet => //returns uint detachId
            {
                string action = string.Empty;
                for (int i = 0; i <= 2; i++)
                    action = packet.ReadString();

                // Call Packet(header, length) to avoid resizing internal byte array...
                var p = new Packet(2883, action.Length + 10);
                p.WriteString(action);
                p.Write(0);
                p.Write(0);
                await interceptor.SendToServerAsync(p);
            });

            interceptor.Start();
            await Task.Delay(-1);
        }
    }
}
