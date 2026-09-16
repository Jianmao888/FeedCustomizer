namespace FeedCustomizer.Core.Models
{
    /// <summary>
    /// Provider 注册操作的稳定结果类别。UI 层据此决定提示、引导授权或恢复界面状态，
    /// 基础设施层不直接依赖任何窗口或对话框实现。
    /// </summary>
    public enum ProviderRegistrationStatus
    {
        Success,
        DeveloperModeConfirmationRequired,
        ElevationCancelled,
        Failed
    }

    /// <summary>
    /// Provider 注册操作的结果与可安全展示的诊断信息。
    /// </summary>
    public sealed record ProviderRegistrationResult(
        ProviderRegistrationStatus Status,
        int? ExitCode,
        string Error,
        string Output,
        string ManifestPath)
    {
        /// <summary>操作是否已经完成注册。</summary>
        public bool Succeeded => Status == ProviderRegistrationStatus.Success;

        /// <summary>
        /// 创建成功结果；成功路径不携带基础设施诊断，避免上层误将旧错误展示给用户。
        /// </summary>
        public static ProviderRegistrationResult Success(string manifestPath) =>
            new(ProviderRegistrationStatus.Success, null, string.Empty, string.Empty, manifestPath);

        /// <summary>
        /// 创建可由 UI 层转换为提示的失败结果，保留退出码和输出以便诊断。
        /// </summary>
        public static ProviderRegistrationResult Failed(
            string manifestPath,
            int? exitCode,
            string error,
            string output) =>
            new(ProviderRegistrationStatus.Failed, exitCode, error, output, manifestPath);
    }
}
