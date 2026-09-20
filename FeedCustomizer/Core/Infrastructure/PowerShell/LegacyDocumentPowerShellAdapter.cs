using FeedCustomizer.Core.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FeedCustomizer.Core.Infrastructure.PowerShell;

/// <summary>
/// 只负责清理真实 LocalAppData 中的旧版 HelpDoc。此适配器不参与文档同步，
/// 且仅由文档协调服务在确认应用更新后调用，普通启动和文件缺失自愈不会创建 PowerShell 进程。
/// </summary>
internal sealed class LegacyDocumentPowerShellAdapter(IPowerShellExecutor executor)
{
    internal async Task<bool> RemoveLegacyHelpAsync(CancellationToken cancellationToken = default)
    {
        var script = new PowerShellScript(
            "RemoveLegacyApplicationDocuments.ps1",
            """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FeedCustomProvider'
            $target = [IO.Path]::GetFullPath((Join-Path $root 'HelpDoc'))
            $root = [IO.Path]::GetFullPath($root).TrimEnd('\')

            # 目标必须是固定根目录的直接子目录；即使未来常量被误改，也不能扩大递归删除范围。
            if (-not $target.Equals(($root + '\HelpDoc'), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Legacy document cleanup target is invalid.'
            }

            # 根目录和目标目录都拒绝重解析点，防止链接把删除操作导向应用目录之外。
            foreach ($path in @($root, $target)) {
                if ((Test-Path -LiteralPath $path) -and
                    ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                    throw "Reparse point is not allowed: $path"
                }
            }

            if (-not (Test-Path -LiteralPath $target)) {
                'False'
                exit 0
            }

            foreach ($item in Get-ChildItem -LiteralPath $target -Recurse -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Reparse point is not allowed: $($item.FullName)"
                }
            }

            Remove-Item -LiteralPath $target -Recurse -Force
            'True'
            """,
            Timeout: TimeSpan.FromSeconds(30));
        PowerShellResult result = await executor.ExecuteAsync(script, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new IOException(
                $"旧文档清理脚本失败：ExitCode={result.ExitCode}; Error={result.Error}; Output={result.Output}");
        }

        return string.Equals(result.Output.Trim(), "True", StringComparison.OrdinalIgnoreCase);
    }
}
