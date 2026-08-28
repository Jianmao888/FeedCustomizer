namespace FeedCustomizer.Core.Models
{
    internal sealed record PowerShellResult(int ExitCode, string Output, string Error);
}
