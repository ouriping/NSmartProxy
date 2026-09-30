using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace NSmartProxy.Infrastructure
{
    /// <summary>
    /// 在 TcpClient 上挂接 TLS 等替代流。未挂接时回退到原始网络流。
    /// </summary>
    public static class TcpTransport
    {
        private static readonly ConditionalWeakTable<TcpClient, Stream> Streams =
            new ConditionalWeakTable<TcpClient, Stream>();

        public static void Attach(TcpClient client, Stream stream)
        {
            Streams.Add(client, stream);
        }

        public static Stream Open(this TcpClient client)
        {
            Stream stream;
            if (Streams.TryGetValue(client, out stream))
            {
                return stream;
            }

            return client.GetStream();
        }
    }
}
