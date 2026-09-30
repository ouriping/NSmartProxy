using System;
using System.Diagnostics;
using System.Dynamic;
using System.Text;
using Newtonsoft.Json;
using NSmartProxy.Data;
using System.IO;

namespace NSmartProxy.Infrastructure
{
    public static class ConfigHelper
    {
        public static string AppSettingFullPath
        {
            get
            {
                var processModule = Process.GetCurrentProcess().MainModule;
                var path1 =Path.GetDirectoryName(processModule?.FileName)
                       + Path.DirectorySeparatorChar
                       + "appsettings.json";
                var path2 = "./appsettings.json";
                if (File.Exists(path1))
                {
                    return path1;
                }
                else
                {
                    return path2;
                }
            }
        }

        /// <summary>
        /// 读配置
        /// </summary>
        /// <returns></returns>
        public static T ReadAllConfig<T>(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open))
            {
                StreamReader sr = new StreamReader(fs);
                var str = StripJsonComments(sr.ReadToEnd());
                try
                {
                    return JsonConvert.DeserializeObject<T>(str);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        "配置文件 " + path + " 无法解析。请修正 JSON，程序不会因此回退到更宽松的默认配置。", ex);
                }
            }
        }

        /// <summary>
        /// 去掉字符串和注释之外的 //、/* */，避免带注释的配置直接导致启动失败。
        /// </summary>
        public static string StripJsonComments(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            var sb = new StringBuilder(json.Length);
            bool inString = false;
            bool escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
                {
                    i += 2;
                    while (i < json.Length && json[i] != '\n')
                    {
                        i++;
                    }

                    if (i < json.Length)
                    {
                        sb.Append('\n');
                    }

                    continue;
                }

                if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/'))
                    {
                        i++;
                    }

                    i++;
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 存配置
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        public static T SaveChanges<T>(this T config, string path)
        {
            JsonSerializer serializer = new JsonSerializer();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                StreamWriter sw = new StreamWriter(fs);
                JsonTextWriter jsonWriter = new JsonTextWriter(sw)
                {
                    Formatting = Formatting.Indented,
                    Indentation = 4,
                    IndentChar = ' '
                };
                serializer.Serialize(jsonWriter, config);

                sw.Close();
            }

            return config;
        }
    }
}