using System;
using System.Threading.Tasks;

using Interceptor;
using Interceptor.Attributes;
using Interceptor.Communication;
using Interceptor.Logging;

namespace InterceptorTest
{
    [Packet(hash: 13792703359221814)]
    public class TalkPacket
    {
        public string Text { get; set; }
        public int Bubble { get; set; }
        public int Count { get; set; }
    }

    [Packet(hash: 27303304672772198)]
    public class WalkPacket
    {
        public int X { get; set; }
        public int Y { get; set; }
    }

    internal class Program
    {
        private static readonly HabboInterceptor interceptor = new HabboInterceptor();
        internal static async Task Main()
        {
            Console.Title = "InterceptorTest";

            interceptor.Log += Log;
            interceptor.Incoming += Incoming;
            interceptor.Outgoing += Outgoing;
            interceptor.DisassembleCompleted += DisassembleCompleted;

            interceptor.WaitForDisassemble = false;

            interceptor.Start();
            await Task.Delay(-1);
        }

        private static Task Outgoing(Packet packet)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("-> {0}", packet);
            return Task.CompletedTask;
        }

        private static Task Incoming(Packet packet)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("<- {0}", packet);
            return Task.CompletedTask;
        }

        private static Task Log(LogMessage message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message.ToString());
            return Task.CompletedTask;
        }

        private static Task DisassembleCompleted()
        {
            Console.Beep();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Disassembled completed. You'll now see hashes and packet structures!");

            interceptor.OutgoingAttach<TalkPacket>(p =>
            {
                p.Text = "HabboInterceptor";
                return Task.FromResult(true);
            });

            interceptor.OutgoingAttach<WalkPacket>(p =>
            {
                interceptor.SendToServerAsync(new TalkPacket
                {
                    Text = $"I'm moving to X: {p.X} Y: {p.Y}.",
                    Bubble = 2
                });
                return Task.FromResult(true);
            });

            return Task.CompletedTask;
        }
    }
}
