using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using log4net;
using log4net.Config;
using Microsoft.Extensions.Configuration;
using NSmartProxy.Client;
using NSmartProxy.Data;
using NSmartProxy.Data.Models;
using NSmartProxy.Infrastructure;
using NSmartProxy.Interfaces;
using NSmartProxy.Shared;

namespace NSmartProxyWinService
{
    public partial class NSPClientService : ServiceBase
    {
        public NSPClientService()
        {
            InitializeComponent();
        }

        protected override void OnStop()
        {
            _ = ClientRouter.Close();
        }

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
        public Router ClientRouter;
        private string appSettingFilePath;
        protected override void OnStart(string[] args)
        {
            string assemblyFilePath = Assembly.GetExecutingAssembly().Location;
            string assemblyDirPath = Path.GetDirectoryName(assemblyFilePath);
            string configFilePath = assemblyDirPath + "\\log4net.config";
            appSettingFilePath = assemblyDirPath + "\\appsettings.json";

            //log
            var loggerRepository = LogManager.CreateRepository("NSmartClientRouterRepository");
            XmlConfigurator.Configure(loggerRepository, new FileInfo(configFilePath));
            NSPClientService.Logger = LogManager.GetLogger(loggerRepository.Name, "NSPClientService");
            if (!loggerRepository.Configured) throw new Exception("log4net配置失败。log config failed.");
            Console.ForegroundColor = ConsoleColor.Yellow;

            //口令只从环境变量读取，避免出现在进程列表里。
            if (args != null && Array.Exists(args, a => a == "-p" || a == "-pwd" || a == "--password" || a == "-u"))
            {
                Logger.Error("不要在服务参数里传入用户名或密码。请设置环境变量 NSP_USERNAME 和 NSP_PASSWORD。");
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

            var builder = new ConfigurationBuilder()
               .SetBasePath(Directory.GetCurrentDirectory())
               .AddJsonFile(appSettingFilePath);

            Configuration = builder.Build();

            //start clientrouter
            //windows服务使用getresult会导致死锁？
            Task.Run(() =>
            {
                try
                {
                    var tsk = StartClient().ConfigureAwait(false);
                    tsk.GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    Logger.Error(e.Message);
                }
            });
         
            Console.Read();
            Logger.Info("Client terminated,press any key to continue.");

        }

        private async Task StartClient()
        {

            ClientRouter = new Router(new Log4netLogger());
            //read config from config file.
            ClientRouter.SetConfiguration(ConfigHelper.ReadAllConfig<NSPClientConfig>(appSettingFilePath));
            if (_currentLoginInfo != null)
            {
                ClientRouter.SetLoginInfo(_currentLoginInfo);
            }

            Task tsk = ClientRouter.Start(true);
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


        /// <summary>
        /// 调试用
        /// </summary>
        /// <param name="args"></param>
        internal void TestStartupAndStop(string[] args)
        {
            this.OnStart(args);
            Console.ReadLine();
            this.OnStop();
        }
    }
}
