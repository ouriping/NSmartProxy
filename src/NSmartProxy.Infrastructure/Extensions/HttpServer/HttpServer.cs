using System;
using System.Collections.Generic;
//using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSmartProxy.Authorize;
using NSmartProxy.Data;
using NSmartProxy.Data.DTOs;
using NSmartProxy.Database;
using NSmartProxy.Infrastructure;
using NSmartProxy.Infrastructure.Interfaces;
using NSmartProxy.Interfaces;
using NSmartProxy.Shared;

// ReSharper disable All

namespace NSmartProxy.Infrastructure.Extension
{
    public partial class HttpServer
    {
        #region HTTPServer

        public INSmartLogger Logger;
        public IDbOperator Dbop;

        private const string INDEX_PAGE = "/main.html";
        private const string BASE_FILE_PATH = "./Web/";
        //private const string BASE_LOG_FILE_PATH = "./log";

        public Dictionary<string, MemoryStream> FilesCache = new Dictionary<string, MemoryStream>(20);
        public IServerContext ServerContext { get; }
        public IWebController ControllerInstance;

        public HttpServer(INSmartLogger logger, IDbOperator dbop, IServerContext serverContext, IWebController controllerInstance)
        {
            Logger = logger;
            Dbop = dbop;
            //第一次加载所有mime类型
            PopulateMappings();
            ServerContext = serverContext;
            ControllerInstance = controllerInstance;
        }



        /// <summary>
        /// http服务启动，初始化代码写在这里
        /// </summary>
        /// <param name="ctsHttp"></param>
        /// <param name="WebManagementPort"></param>
        /// <returns></returns>
        public async Task StartHttpService(CancellationTokenSource ctsHttp, int WebManagementPort, string webApiAddress)
        {
            try
            {
                HttpListener listener = new HttpListener();
                //缓存所有文件
                var dir = new DirectoryInfo(BASE_FILE_PATH);
                var files = dir.GetFiles("*.*");
                foreach (var file in files)
                {
                    using (var fs = file.OpenRead())
                    {
                        var mms = new MemoryStream();
                        fs.CopyTo(mms);
                        FilesCache.Add(file.Name, mms);
                    }
                }
                Logger.Debug($"{files.Length} files cached.");

                var host = string.IsNullOrWhiteSpace(webApiAddress) ? "127.0.0.1" : webApiAddress.Trim();
                string prefix;
                if (host == "0.0.0.0" || host == "*" || host == "+")
                {
                    prefix = $"http://+:{WebManagementPort}/";
                }
                else
                {
                    prefix = $"http://{host}:{WebManagementPort}/";
                }

                listener.Prefixes.Add(prefix);
                Logger.Debug("Listening HTTP request on port " + WebManagementPort.ToString() + "...");
                await AcceptHttpRequest(listener, ctsHttp);
            }
            catch (HttpListenerException ex)
            {
                Logger.Debug("Please run this program in administrator mode." + ex);
                Logger.Error(ex.ToString(), ex);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex);
                Logger.Error(ex.ToString(), ex);
            }
            Logger.Debug("Http服务结束。");
        }

        private async Task AcceptHttpRequest(HttpListener httpService, CancellationTokenSource ctsHttp)
        {
            httpService.Start();
            while (true)
            {
                var client = await httpService.GetContextAsync();
                _ = ProcessHttpRequestAsync(client);
            }
        }

        private async Task ProcessHttpRequestAsync(HttpListenerContext context)
        {

            var request = context.Request;
            var response = context.Response;
            ControllerInstance.SetContext(context);
            //context上下文设置给WebContext
            //TODO XX 设置该同源策略为了方便调试，真实项目请确保同源

#if DEBUG
            response.AddHeader("Access-Control-Allow-Origin", "*");
#endif
            response.Headers["Server"] = "";
            try
            {
                //通过request来的值进行接口调用
                string unit = request.RawUrl.Replace("//", "");

                if (unit == "/") unit = INDEX_PAGE;

                int idx1 = unit.LastIndexOf("#");
                if (idx1 > 0) unit = unit.Substring(0, idx1);
                int idx2 = unit.LastIndexOf("?");
                if (idx2 > 0) unit = unit.Substring(0, idx2);
                int idx3 = unit.LastIndexOf(".");

                //通过后缀获取不同的文件，若无后缀，则调用接口
                if (idx3 > 0)
                {

                    if (!File.Exists(BASE_FILE_PATH + unit))
                    {
                        Logger.Debug($"未找到文件{BASE_FILE_PATH + unit}");
                        return;

                    }
                    //mime类型
                    ProcessMIME(response, unit.Substring(idx3));
                    if (!IsPublicWebFile(unit))
                    {
                        if (CurrentSession(request) == null)
                        {
                            response.StatusCode = 302;
                            response.Headers["Location"] = "/login.html";
                            return;
                        }
                    }

                    //读文件优先去缓存读
                    if (FilesCache.TryGetValue(unit.TrimStart('/'), out MemoryStream memoryStream))
                    {
                        memoryStream.Position = 0;
                        await memoryStream.CopyToAsync(response.OutputStream);
                    }
                    else
                    {
                        using (FileStream fs = new FileStream(BASE_FILE_PATH + unit, FileMode.Open))
                        {
                            await fs.CopyToAsync(response.OutputStream);
                        }
                    }
                }
                else  //url中没有小数点则是接口
                {
                    unit = unit.Replace("/", "");
                    response.ContentEncoding = Encoding.UTF8;

                    //调用接口 用分布类隔离并且用API特性限定安全
                    object jsonObj;
                    object[] parameters = null;

                    //反射调用API方法
                    MethodInfo method = null;
                    try
                    {
                        method = ControllerInstance.GetType().GetMethod(unit);
                        if (method == null)
                        {
                            //Server.Logger.Debug($"无效的方法名{unit}");
                            throw new Exception($"无效的方法名{unit}");
                        }

                        var takesPassword = false;
                        foreach (var parameter in method.GetParameters())
                        {
                            if (parameter.Name != null
                                && parameter.Name.IndexOf("pwd", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                takesPassword = true;
                                break;
                            }
                        }

                        if (takesPassword
                            && !string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception("包含口令的接口只接受 POST。");
                        }

                        if (method.GetCustomAttribute<FileUploadAttribute>() == null)
                        {
                            parameters = BindParameters(method, request, allowQuery: !takesPassword);
                        }

                        var session = CurrentSession(request);
                        if (method.GetCustomAttribute<SecureAttribute>() != null)
                        {
                            if (session == null)
                            {
                                throw new Exception("用户未登录。");
                            }

                            if (session.IsAnonymous)
                            {
                                throw new Exception("匿名用户不能访问管理接口。");
                            }

                            if (method.GetCustomAttribute<AdminOnlyAttribute>() != null && !session.IsAdmin)
                            {
                                throw new Exception("需要管理员权限。");
                            }
                        }

                        if (method.GetCustomAttribute<APIAttribute>() != null)
                        {
                            //返回json，类似WebAPI
                            response.ContentType = "application/json";
                            jsonObj = method.Invoke(ControllerInstance, parameters);
                            await response.OutputStream.WriteAsync(HtmlUtil.GetContent(jsonObj.Wrap().ToJsonString()));
                        }
                        else if (method.GetCustomAttribute<FormAPIAttribute>() != null)
                        {
                            //返回表单页面
                            response.ContentType = "text/html";
                            jsonObj = method.Invoke(ControllerInstance, parameters);
                            await response.OutputStream.WriteAsync(HtmlUtil.GetContent(jsonObj.ToString()));
                        }
                        else if (method.GetCustomAttribute<ValidateAPIAttribute>() != null)
                        {
                            //验证模式
                            response.ContentType = "application/json";
                            bool validateResult = (bool)method.Invoke(ControllerInstance, parameters);
                            if (validateResult == true)
                            {
                                await response.OutputStream.WriteAsync(HtmlUtil.GetContent("{\"valid\":true}"));
                            }
                            else
                            {
                                await response.OutputStream.WriteAsync(HtmlUtil.GetContent("{\"valid\":false}"));
                            }
                        }
                        else if (method.GetCustomAttribute<FileAPIAttribute>() != null)
                        {
                            //文件下载
                            response.ContentType = "application/octet-stream";
                            if (!(method.Invoke(ControllerInstance, parameters) is FileDTO fileDto))
                            {
                                throw new Exception("文件返回失败，请查看错误日志。");
                            }

                            response.Headers.Add("Content-Disposition", "attachment;filename=" + fileDto.FileName);
                            //response.OutputStream.(stream);
                            using (fileDto.FileStream)
                            {
                                await fileDto.FileStream.CopyToAsync(response.OutputStream);
                            }
                        }
                        else if (method.GetCustomAttribute<FileUploadAttribute>() != null)
                        {
                            //文件上传
                            response.ContentType = "application/json";
                            if (request.HttpMethod.ToUpper() == "POST")
                            {
                                List<object> paraList = new List<object>();
                                var fileName = SaveFile(request.ContentEncoding, request.ContentType, request.InputStream);
                                paraList.Add(new FileInfo($"./{fileName}"));
                                if (parameters != null)
                                    paraList.AddRange(parameters);
                                jsonObj = method.Invoke(ControllerInstance, paraList.ToArray());
                                await response.OutputStream.WriteAsync(HtmlUtil.GetContent(jsonObj.Wrap().ToJsonString()));
                            }
                            else
                            {
                                await response.OutputStream.WriteAsync(HtmlUtil.GetContent(HttpResult<object>.NullSuccessResult.ToJsonString()));
                            }


                            //request.
                        }
                    }
                    catch (Exception ex)
                    {
                        if ((ex is TargetInvocationException) && ex.InnerException != null) ex = ex.InnerException;
                        Logger.Error(ex.Message, ex);
                        jsonObj = new Exception(ex.Message);
                        response.ContentType = "application/json";
                        await response.OutputStream.WriteAsync(HtmlUtil.GetContent(jsonObj.Wrap().ToJsonString()));
                    }
                    finally
                    {

                    }
                }

            }
            catch (Exception e)
            {
                Logger.Error(e.Message, e);
                throw;
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private static readonly HashSet<string> PublicWebFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/login.html",
            "/signin.css",
            "/bootstrap.min.css",
            "/jquery-slim.min.js",
            "/main.js",
            "/favicon.ico"
        };

        private static bool IsPublicWebFile(string unit)
        {
            if (string.IsNullOrEmpty(unit))
            {
                return false;
            }

            var path = unit.StartsWith("/") ? unit : "/" + unit;
            return PublicWebFiles.Contains(path);
        }

        private AuthSession CurrentSession(HttpListenerRequest request)
        {
            var store = ServerContext as IAuthSessionStore;
            if (store == null || request.Cookies[Global.TOKEN_COOKIE_NAME] == null)
            {
                return null;
            }

            return store.GetSession(request.Cookies[Global.TOKEN_COOKIE_NAME].Value);
        }

        private object[] BindParameters(MethodInfo method, HttpListenerRequest request, bool skipBody = false, bool allowQuery = true)
        {
            var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!skipBody
                && string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                && request.HasEntityBody
                && request.ContentType != null
                && request.ContentType.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                {
                    foreach (var pair in ParseForm(reader.ReadToEnd()))
                    {
                        form[pair.Key] = pair.Value;
                    }
                }
            }

            var methodParams = method.GetParameters();
            var parameters = new object[methodParams.Length];
            for (int i = 0; i < methodParams.Length; i++)
            {
                var name = methodParams[i].Name;
                string value = null;
                if (name != null && form.TryGetValue(name, out value))
                {
                    parameters[i] = value;
                    continue;
                }

                if (allowQuery)
                {
                    var fromQuery = request.QueryString[name];
                    if (fromQuery != null)
                    {
                        parameters[i] = fromQuery;
                        continue;
                    }

                    if (i < request.QueryString.Count)
                    {
                        parameters[i] = request.QueryString[i];
                        continue;
                    }
                }

                parameters[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : null;
            }

            return parameters;
        }

        private static Dictionary<string, string> ParseForm(string body)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(body))
            {
                return dict;
            }

            foreach (var pair in body.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }

                var idx = pair.IndexOf('=');
                string key;
                string val;
                if (idx < 0)
                {
                    key = Uri.UnescapeDataString(pair.Replace('+', ' '));
                    val = "";
                }
                else
                {
                    key = Uri.UnescapeDataString(pair.Substring(0, idx).Replace('+', ' '));
                    val = Uri.UnescapeDataString(pair.Substring(idx + 1).Replace('+', ' '));
                }

                dict[key] = val;
            }

            return dict;
        }

        private void ProcessMIME(HttpListenerResponse response, string suffix)
        {
            if (suffix == ".html" || suffix == ".js")
            {
                response.ContentEncoding = Encoding.UTF8;
            }

            if (_mimeMappings.TryGetValue(suffix, out string val))
            {
                // found!
                response.ContentType = val;
            }
            else
            {
                response.ContentType = "application/octet-stream";
            }

        }



        #region 文件读取
        private String GetBoundary(String ctype)
        {
            return "--" + ctype.Split(';')[1].Split('=')[1];
        }

        private string SaveFile(Encoding enc, String contentType, Stream input)
        {

            Byte[] boundaryBytes = enc.GetBytes(GetBoundary(contentType));
            Int32 boundaryLen = boundaryBytes.Length;
            string fileName = Guid.NewGuid().ToString("N") + ".temp";
            using (FileStream output = new FileStream(fileName, FileMode.Create, FileAccess.Write))
            {
                Byte[] buffer = new Byte[1024];
                Int32 len = input.Read(buffer, 0, 1024);
                Int32 startPos = -1;

                // Find start boundary
                while (true)
                {
                    if (len == 0)
                    {
                        throw new Exception("Start Boundaray Not Found");
                    }

                    startPos = IndexOf(buffer, len, boundaryBytes);
                    if (startPos >= 0)
                    {
                        break;
                    }
                    else
                    {
                        Array.Copy(buffer, len - boundaryLen, buffer, 0, boundaryLen);
                        len = input.Read(buffer, boundaryLen, 1024 - boundaryLen);
                    }
                }

                // Skip four lines (Boundary, Content-Disposition, Content-Type, and a blank)
                for (Int32 i = 0; i < 4; i++)
                {
                    while (true)
                    {
                        if (len == 0)
                        {
                            throw new Exception("Preamble not Found.");
                        }

                        startPos = Array.IndexOf(buffer, enc.GetBytes("\n")[0], startPos);
                        if (startPos >= 0)
                        {
                            startPos++;
                            break;
                        }
                        else
                        {
                            len = input.Read(buffer, 0, 1024);
                        }
                    }
                }

                Array.Copy(buffer, startPos, buffer, 0, len - startPos);
                len = len - startPos;

                while (true)
                {
                    Int32 endPos = IndexOf(buffer, len, boundaryBytes);
                    if (endPos >= 0)
                    {
                        if (endPos > 0) output.Write(buffer, 0, endPos - 2);
                        break;
                    }
                    else if (len <= boundaryLen)
                    {
                        throw new Exception("End Boundaray Not Found");
                    }
                    else
                    {
                        output.Write(buffer, 0, len - boundaryLen);
                        //每次放置后40个字节到首部，读取接下来984个字节，在此基础上进行byte查找，绝妙！
                        Array.Copy(buffer, len - boundaryLen, buffer, 0, boundaryLen);
                        len = input.Read(buffer, boundaryLen, 1024 - boundaryLen) + boundaryLen;
                    }
                }
            }

            return fileName;
        }

        private Int32 IndexOf(Byte[] buffer, Int32 len, Byte[] boundaryBytes)
        {
            for (Int32 i = 0; i <= len - boundaryBytes.Length; i++)
            {
                Boolean match = true;
                for (Int32 j = 0; j < boundaryBytes.Length && match; j++)
                {
                    match = buffer[i + j] == boundaryBytes[j];
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
        #endregion

        #endregion

    }
}
