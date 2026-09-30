using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NSmartProxy.Infrastructure
{
    public static class FileModeHelper
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string pathname, int mode);

        /// <summary>
        /// 在 Unix 上把文件收成仅当前用户可读写（0600）。
        /// </summary>
        public static void RestrictToOwner(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                chmod(path, 384);
            }
            catch (Exception)
            {
                // 权限收紧失败不应阻止进程启动，调用方仍应避免把文件放到共享目录。
            }
        }
    }
}
