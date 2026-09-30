using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;
using NSmartProxy.Client;
using NSmartProxy.Data;
using NSmartProxy.Data.Models;
using NSmartProxy.Interfaces;
using NSmartProxy.Shared;
using System;
using System.IO;
using System.Threading.Tasks;
using FtpServer;
using System.Net;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace NSmartProxyFTP
{
    class Program
    {
        #region logger
        public class Log4netLogger : INSmartLogger
        {
            public void Debug(object message)
            {
                //Logger.Debug(message);
                Logger.Debug(message);
            }

            public void Error(object message, Exception ex)
            {
                //Logger.Debug(message);
                Logger.Error(message, ex);
            }

            public void Info(object message)
            {
                Logger.Info(message);
            }
        }
        #endregion

        public static ILog Logger;
        public static IConfigurationRoot Configuration { get; set; }
        private static LoginInfo _currentLoginInfo;
        static void Main(string[] args)
        {
            //log
            var loggerRepository = LogManager.CreateRepository("NSmartClientRouterRepository");
            XmlConfigurator.Configure(loggerRepository, new FileInfo("log4net.config"));
            Logger = LogManager.GetLogger(loggerRepository.Name, "NSmartProxyFTP");
            if (!loggerRepository.Configured) throw new Exception("log config failed.");
            Console.ForegroundColor = ConsoleColor.Yellow;

            if (args != null && Array.Exists(args, a => a == "-p" || a == "-pwd" || a == "--password" || a == "-u"))
            {
                Console.Error.WriteLine("不要在命令行传入用户名或密码。请设置环境变量 NSP_USERNAME 和 NSP_PASSWORD。");
            }

            var envUser = Environment.GetEnvironmentVariable("NSP_USERNAME");
            var envPwd = Environment.GetEnvironmentVariable("NSP_PASSWORD");
            if (!string.IsNullOrEmpty(envUser))
            {
                _currentLoginInfo = new LoginInfo();
                _currentLoginInfo.UserName = envUser;
                _currentLoginInfo.UserPwd = envPwd ?? "";
            }

            Logger.Info($"*** {NSPVersion.NSmartProxyServerName} ***");

            var builder = new ConfigurationBuilder()
               .SetBasePath(Directory.GetCurrentDirectory())
               .AddJsonFile("appsettings.json");

            Configuration = builder.Build();

            //start clientrouter.
            try
            {
                StartClient().Wait();
            }
            catch (Exception e)
            {
                Logger.Error(e.Message);
            }
            Console.Read();
            Logger.Info("Client terminated,press any key to continue.");

        }

        private static void StartFTP(ClientModel clientModel)
        {
            var ip = GetIP(Configuration.GetSection("ProviderAddress").Value);
            Console.WriteLine("外网IP:" + ip.ToString());
            var users = new List<UserElement>();
            foreach (var u in Configuration.GetSection("FtpUsers").GetChildren())
            {
                var username = u["username"];
                var password = u["password"];
                var rootDir = u["rootDir"] ?? "";
                if (string.IsNullOrEmpty(password)
                    || password == "654123"
                    || password == "change-me"
                    || username == "change-me"
                    || rootDir.Equals(@"d:\", StringComparison.OrdinalIgnoreCase)
                    || (rootDir.Length <= 3 && rootDir.EndsWith(":\\", StringComparison.Ordinal)))
                {
                    throw new Exception("FTP 账号仍是占位口令，或根目录是盘符根。请先修改 appsettings.json。");
                }

                users.Add(new UserElement() { username = username, password = password, rootDir = rootDir });
            }
            var server = new FtpServer.FtpServer(int.Parse(Configuration.GetSection("FtpPort").Value), int.Parse(Configuration.GetSection("PasvPort").Value), int.Parse(Configuration.GetSection("FtpMaxConnect").Value), users);
            server.Start(ip, clientModel.AppList[1].Port);
        }

        private static IPAddress GetIP(string server)
        {
            Regex rx = new Regex(@"((?:(?:25[0-5]|2[0-4]\d|((1\d{2})|([1-9]?\d)))\.){3}(?:25[0-5]|2[0-4]\d|((1\d{2})|([1-9]?\d))))");
            if (!rx.IsMatch(server))
            {
                IPAddress[] ips = Dns.GetHostAddresses(server);
                return ips[0];
            }
            return IPAddress.Parse(server);
        }

        private static async Task StartClient()
        {

            Router clientRouter = new Router(new Log4netLogger());
            //read config from config file.
            SetConfig(clientRouter);
            if (_currentLoginInfo != null)
            {
                clientRouter.SetLoginInfo(_currentLoginInfo);
            }

            Task tsk = clientRouter.Start(true, StartFTP);
            try
            {
                await tsk;
            }
            catch (Exception e)
            {
                Logger.Error(e);
                throw;
            }

        }

        private static void SetConfig(Router clientRouter)
        {

            NSPClientConfig config = new NSPClientConfig();
            config.ProviderAddress = Configuration.GetSection("ProviderAddress").Value;
            config.ProviderWebPort = int.Parse(Configuration.GetSection("ProviderWebPort").Value);
            config.ProviderCertThumbprint = Configuration.GetSection("ProviderCertThumbprint").Value ?? "";
            var tls = Configuration.GetSection("ControlTlsEnabled").Value;
            if (!string.IsNullOrEmpty(tls))
            {
                config.ControlTlsEnabled = bool.Parse(tls);
            }
            var allowDangerous = Configuration.GetSection("AllowDangerousPorts").Value;
            if (!string.IsNullOrEmpty(allowDangerous))
            {
                config.AllowDangerousPorts = bool.Parse(allowDangerous);
            }
            var configClients = Configuration.GetSection("Clients").GetChildren();
            foreach (var cli in configClients)
            {
                int confConsumerPort = 0;
                if (cli["ConsumerPort"] != null) confConsumerPort = int.Parse(cli["ConsumerPort"]);
                config.Clients.Add(new ClientApp
                {
                    IP = cli["IP"],
                    TargetServicePort = int.Parse(cli["TargetServicePort"]),
                    ConsumerPort = confConsumerPort,
                    Host = cli["Host"],
                    Protocol = Enum.Parse<Protocol>((cli["Protocol"] ?? "TCP").ToUpper()),
                    Description = cli["Description"]
                });
            }
            clientRouter.SetConfiguration(config);
        }
    }
}
