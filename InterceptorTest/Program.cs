using System;
using System.Threading.Tasks;

using Interceptor;

namespace InterceptorTest
{
    class Program
    {
        static async Task Main(string[] args)
        {
            HabboInterceptor interceptor = new HabboInterceptor();
            interceptor.Start();

            interceptor.Connected += () => { Console.WriteLine("Connected..."); return Task.CompletedTask; };
            interceptor.Incoming += packet => { Console.WriteLine("<- {0}", packet); return Task.CompletedTask; };
            interceptor.Outgoing += packet => { Console.WriteLine("-> {0}", packet); return Task.CompletedTask; };
            interceptor.Log += message => { Console.WriteLine(message); return Task.CompletedTask; };

            await Task.Delay(-1);
        }
    }
}
