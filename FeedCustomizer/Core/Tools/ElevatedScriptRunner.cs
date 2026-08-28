using FeedCustomizer.Core.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Tools
{
    /// <summary>
    /// 以管理员权限执行一段 PowerShell 脚本，并统一处理临时目录、
    /// 输出捕获与清理。脚本内容中可以直接使用预注入的
    /// $outputPath / $errorPath 变量写入标准输出与错误输出。
    /// </summary>
    internal static class ElevatedScriptRunner
    {
        /// <summary>
        /// 用户取消 UAC 时 Process.Start 抛出的 Win32 错误码（ERROR_CANCELLED）。
        /// </summary>
        public const int ElevationCancelledExitCode = 1223;

        /// <summary>
        /// 将脚本写入临时目录后以 runas 方式执行，返回退出码与输出内容。
        /// </summary>
        public static async Task<PowerShellResult> RunAsync(string scriptFileName, string script)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"FeedCustomizer_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            string scriptPath = Path.Combine(tempDir, scriptFileName);
            string outputPath = Path.Combine(tempDir, "stdout.txt");
            string errorPath = Path.Combine(tempDir, "stderr.txt");

            string escapedOutputPath = outputPath.Replace("'", "''");
            string escapedErrorPath = errorPath.Replace("'", "''");

            string fullScript =
                $"$outputPath = '{escapedOutputPath}'" + Environment.NewLine +
                $"$errorPath = '{escapedErrorPath}'" + Environment.NewLine +
                script;

            await File.WriteAllTextAsync(scriptPath, fullScript);

            try
            {
                return await RunElevatedAsync(scriptPath, outputPath, errorPath);
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }
        }

        private static async Task<PowerShellResult> RunElevatedAsync(
            string scriptPath,
            string outputPath,
            string errorPath)
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);

            try
            {
                using Process? process = Process.Start(startInfo);
                if (process is null)
                {
                    return new PowerShellResult(-1, string.Empty, "无法启动管理员 PowerShell。");
                }

                await process.WaitForExitAsync();
                return new PowerShellResult(
                    process.ExitCode,
                    await ReadAllTextIfExistsAsync(outputPath),
                    await ReadAllTextIfExistsAsync(errorPath));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationCancelledExitCode)
            {
                return new PowerShellResult(
                    ElevationCancelledExitCode,
                    string.Empty,
                    "用户取消了管理员权限请求。");
            }
        }

        private static async Task<string> ReadAllTextIfExistsAsync(string path)
        {
            try
            {
                return File.Exists(path)
                    ? await File.ReadAllTextAsync(path)
                    : string.Empty;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"读取提权输出文件失败：{ex.Message}");
                return string.Empty;
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    //Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理临时目录失败：{ex.Message}");
            }
        }
    }
}
