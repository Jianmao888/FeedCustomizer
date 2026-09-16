using FeedCustomizer.Core.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// 将 Provider 工作副本发布到真实 LocalAppData 的文件适配器。
    /// robocopy 及其恢复脚本被限制在基础设施层，业务层只接收最终清单路径。
    /// </summary>
    internal sealed class ProviderFilesPowerShellAdapter(IPowerShellExecutor executor)
    {
        /// <summary>
        /// 将已验证的 Provider 工作副本发布到部署目录，并返回实际清单路径。
        /// 复制失败时依次执行最小修复与最终的目录重建，避免首次失败就破坏可用部署副本。
        /// </summary>
        internal async Task<string> PublishAsync(
            string source,
            string destination,
            CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException($"源目录不存在：{source}");
            }

            string normalizedSource = NormalizeDirectory(source);
            string normalizedDestination = NormalizeDirectory(destination);
            if (string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase))
            {
                Debug.WriteLine($"源与目标路径相同，跳过复制：{normalizedSource}");
                return ValidateManifest(destination);
            }

            PowerShellScript copyScript = BuildCopyScript(source, destination);
            PowerShellResult firstResult = await executor.ExecuteAsync(copyScript, cancellationToken);
            if (IsRobocopySuccessful(firstResult))
            {
                return ValidateManifest(destination);
            }

            Debug.WriteLine(
                $"robocopy 首次复制失败 ({firstResult.ExitCode})。" +
                $"Output: {firstResult.Output}; Error: {firstResult.Error}");

            // resources.pri 常被资源加载器占用；先只移除该可再生文件，保留其余有效部署内容。
            await DeletePathAsync(
                Path.Combine(destination, "resources.pri"),
                recursive: false,
                cancellationToken);

            PowerShellResult retryResult = await executor.ExecuteAsync(copyScript, cancellationToken);
            if (IsRobocopySuccessful(retryResult))
            {
                return ValidateManifest(destination);
            }

            Debug.WriteLine(
                $"删除 resources.pri 后复制仍失败 ({retryResult.ExitCode})。" +
                $"Output: {retryResult.Output}; Error: {retryResult.Error}");

            // 两次增量复制均失败后才清空已验证的应用专用部署目录，随后立即重建。
            await ClearDirectoryAsync(destination, cancellationToken);
            PowerShellResult finalResult = await executor.ExecuteAsync(copyScript, cancellationToken);
            if (!IsRobocopySuccessful(finalResult))
            {
                throw new IOException(
                    $"无法将包文件复制到 {destination}，robocopy 多次尝试失败：" +
                    $"第一次: {FormatResult(firstResult)} || " +
                    $"重试: {FormatResult(retryResult)} || " +
                    $"最后一次: {FormatResult(finalResult)}");
            }

            return ValidateManifest(destination);
        }

        private static PowerShellScript BuildCopyScript(string source, string destination)
        {
            string script = string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'Stop'",
                $"$source = {PowerShellLiteral.Quote(source)}",
                $"$destination = {PowerShellLiteral.Quote(destination)}",
                "if (-not (Test-Path -LiteralPath $destination)) { New-Item -ItemType Directory -Path $destination | Out-Null }",
                "& robocopy.exe $source $destination /E /COPY:DAT /R:0 /W:0 /MT:8",
                "exit $LASTEXITCODE");
            return new PowerShellScript("PublishProviderFiles.ps1", script);
        }

        private async Task DeletePathAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken)
        {
            string recurseSwitch = recursive ? " -Recurse" : string.Empty;
            string script = string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'SilentlyContinue'",
                $"$path = {PowerShellLiteral.Quote(path)}",
                $"if (Test-Path -LiteralPath $path) {{ Remove-Item -LiteralPath $path -Force{recurseSwitch} }}",
                "exit 0");
            await executor.ExecuteAsync(
                new PowerShellScript("DeleteProviderPath.ps1", script),
                cancellationToken);
        }

        private async Task ClearDirectoryAsync(
            string directory,
            CancellationToken cancellationToken)
        {
            string script = string.Join(
                Environment.NewLine,
                "$ErrorActionPreference = 'SilentlyContinue'",
                $"$directory = {PowerShellLiteral.Quote(directory)}",
                "if (Test-Path -LiteralPath $directory) {",
                "    Get-ChildItem -LiteralPath $directory -Force | Remove-Item -Force -Recurse",
                "}",
                "exit 0");
            await executor.ExecuteAsync(
                new PowerShellScript("ClearProviderDirectory.ps1", script),
                cancellationToken);
        }

        private static bool IsRobocopySuccessful(PowerShellResult result) =>
            result.ExitCode >= 0 && result.ExitCode < 8;

        private static string FormatResult(PowerShellResult result) =>
            $"ExitCode={result.ExitCode}; Output={result.Output}; Error={result.Error}";

        private static string NormalizeDirectory(string path) =>
            Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

        private static string ValidateManifest(string destination)
        {
            string manifestPath = Path.Combine(destination, "AppxManifest.xml");
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"目标路径缺少清单文件：{manifestPath}");
            }

            return manifestPath;
        }
    }
}
