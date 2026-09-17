namespace FeedCustomizer.Core.Infrastructure.PowerShell
{
    /// <summary>
    /// 基础设施组合入口。集中复用同一个无状态执行器，并向业务层暴露语义适配器。
    /// </summary>
    internal static class PowerShellInfrastructure
    {
        private static readonly IPowerShellExecutor Executor = new PowerShellProcessExecutor();

        internal static AppxPackagePowerShellAdapter AppxPackages { get; } = new(Executor);

        internal static ProviderDeploymentPowerShellAdapter DeploymentFiles { get; } = new(Executor);

        internal static DeveloperModePowerShellAdapter DeveloperMode { get; } = new(Executor);

        internal static RegionPolicyPowerShellAdapter RegionPolicy { get; } = new(Executor);

        internal static WidgetDataPowerShellAdapter WidgetData { get; } = new(Executor);
    }
}
