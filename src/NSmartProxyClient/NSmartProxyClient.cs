using log4net;
using Microsoft.Extensions.Configuration;
using NSmartProxy.Client;
using NSmartProxy.Data;
using NSmartProxy.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net.Config;
using NSmartProxy.Data.Models;
using Exception = System.Exception;
using NSmartProxy.Shared;
using Protocol = NSmartProxy.Data.Protocol;
using NSmartProxy.Infrastructure;

namespace NSmartProxy
{
    class NSmartProxyClient
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
        private static readonly string ConfigFilePath = ConfigHelper.AppSettingFullPath;
        public void Start(string[] args)
        {
            //appSettingFilePath = Directory.GetCurrentDirectory() + "/appsettings.json";
            //log
            var loggerRepository = LogManager.CreateRepository("NSmartClientRouterRepository");
            XmlConfigurator.Configure(loggerRepository, new FileInfo("log4net.config"));
            NSmartProxyClient.Logger = LogManager.GetLogger(loggerRepository.Name, "NSmartServerClient");
            if (!loggerRepository.Configured) throw new Exception("log config failed.");
            Console.ForegroundColor = ConsoleColor.Yellow;

            //口令只从环境变量读取，避免出现在进程列表里。
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

            Logger.Info($"*** {NSPVersion.NSmartProxyClientName} ***");

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

        private static async Task StartClient()
        {

            Router clientRouter = new Router(new Log4netLogger());
            //read config from config file.
            clientRouter.SetConfiguration(ConfigHelper.ReadAllConfig<NSPClientConfig>(ConfigFilePath));
            if (_currentLoginInfo != null)
            {
                clientRouter.SetLoginInfo(_currentLoginInfo);
            }

            Task tsk = clientRouter.Start(true);
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

        public void Stop()
        {
            //
            Console.WriteLine(NSPVersion.NSmartProxyServerName + " STOPPED.");
            Environment.Exit(0);
        }
    }
}
