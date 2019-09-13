﻿using System;
using System.Threading.Tasks;

using Interceptor;
using Interceptor.Attributes;

namespace InterceptorTest
{
    [Packet(hash: 13792703359221814)]//"68d1be")]
    public class TalkPacket
    {
        public string Text { get; set; }
        public int Unk1 { get; set; }
        public int Unk2 { get; set; }
    }

    [Packet(hash: 27303304672772198)]//"f76a21")]
    public class WalkPacket
    {
        public int X { get; set; }
        public int Y { get; set; }
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

            interceptor.OutgoingAttach<TalkPacket>(p =>
            {
                p.Text = "HabboInterceptor";
                return Task.FromResult(true);
            });

            interceptor.OutgoingAttach<WalkPacket>(p =>
            {
                interceptor.SendToServerAsync(new TalkPacket
                {
                    Text = $"I'm moving to X: {p.X} Y: {p.Y}."
                });
                return Task.FromResult(true);
            });

            interceptor.Start();
            await Task.Delay(-1);
        }
    }
}
