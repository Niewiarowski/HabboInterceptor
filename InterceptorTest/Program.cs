using System;
using System.Threading.Tasks;

using Interceptor;
using Interceptor.Communication;

namespace InterceptorTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Title = "InterceptorTest";
            HabboInterceptor interceptor = new HabboInterceptor();

            interceptor.Connected += () =>
            {
                return Task.CompletedTask;
            };
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

            interceptor.Start();
            await Task.Delay(-1);
        }
    }
}
