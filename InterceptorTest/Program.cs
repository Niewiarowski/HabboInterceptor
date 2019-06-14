using System;
using System.Threading.Tasks;

using Interceptor;
using Interceptor.Memory.Extensions;

namespace InterceptorTest
{
    public class TalkPacket
    {
        public string Text { get; set; }
        public int Unk1 { get; set; }
        public int Unk2 { get; set; }
    }

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

            interceptor.OutgoingAttach<TalkPacket>(p => p.Hash.EqualsString("68d1be"), p =>
            {
                p.Text = "HabboInterceptor";
                return Task.FromResult(true);
            });

            interceptor.Start();
            await Task.Delay(-1);
        }
    }
}
