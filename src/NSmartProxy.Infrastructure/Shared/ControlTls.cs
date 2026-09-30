using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace NSmartProxy.Infrastructure
{
    /// <summary>
    /// 客户端与服务端控制通道、反向隧道共用的 TLS。
    /// 服务端设置 ServerCertificate；客户端设置 ClientEnabled 与指纹。
    /// </summary>
    public static class ControlTls
    {
        public static X509Certificate2 ServerCertificate { get; set; }
        public static bool ClientEnabled { get; set; }
        public static string ExpectedThumbprint { get; set; }

        public static async Task HandshakeAsServerAsync(TcpClient client)
        {
            if (ServerCertificate == null)
            {
                return;
            }

            var ssl = new SslStream(client.GetStream(), false);
#if NETSTANDARD2_0
            await ssl.AuthenticateAsServerAsync(ServerCertificate);
#else
            await ssl.AuthenticateAsServerAsync(ServerCertificate, false, SslProtocols.Tls12, false);
#endif
            TcpTransport.Attach(client, ssl);
        }

        public static async Task HandshakeAsClientAsync(TcpClient client, string host)
        {
            if (!ClientEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(ExpectedThumbprint))
            {
                throw new InvalidOperationException(
                    "控制通道已启用 TLS，但未配置 ProviderCertThumbprint。请把服务端启动时打印的证书指纹写入客户端配置。");
            }

            var expected = ExpectedThumbprint.Replace(" ", "").Trim();
            var ssl = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) =>
            {
                var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);
                var actual = (cert2.Thumbprint ?? "").Replace(" ", "");
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            });
            await ssl.AuthenticateAsClientAsync(host);
            TcpTransport.Attach(client, ssl);
        }
    }
}
