# 应用日志设计

## 存储与保留

应用日志写入 `AppDataPaths.PackageLocalLogPath`，该路径位于 MSIX 应用私有数据目录。文件名固定使用 `FeedCustomizer-*.log` 前缀，并由 Serilog 按以下规则滚动和清理：

- 每天创建新的日志文件；
- 单个文件最大 2 MiB，达到上限后在当天分卷；
- 最多保留 28 个文件；
- 最长保留 14 天；
- 保留机制只管理 `FeedCustomizer-*.log`，不删除 `RegionPolicyError` 等其他诊断文件。

日志使用同步写入，优先保证应用异常退出前的诊断完整性。日志目录或 Sink 初始化失败时，应用会降级为只写 Visual Studio 调试输出，不得影响启动、Provider 部署或其他业务结果。

## 生命周期

`AppLog` 是进程级日志生命周期入口，在 `App` 构造期间、XAML 初始化之前建立。配置建立后不再允许业务代码修改。正常关闭主窗口、辅助实例退出或进程退出时会执行刷新和关闭；重复关闭是安全的。

每次启动生成一个短 `SessionId`。启动环境日志包含应用包版本、Windows 产品与内部版本、进程和系统架构、.NET、WinUI、AppLifecycle 程序集版本及界面区域，用于关联同一次运行中的跨层事件。

## 调用约束

调用方通过 `AppLog.For<T>()` 或 `AppLog.For(string)` 获取只读的 `IAppLog`，使用结构化字段记录操作编号、阶段、退出码、耗时和结果。例如 Provider 部署的同一次应用操作使用固定 `OperationId`，PowerShell 执行记录脚本语义名称而不记录脚本正文。

原有 Visual Studio 调试体验由 Serilog Debug Sink 保留。同一条普通日志同时进入 `.log` 文件和调试输出；只有可能包含用户图片文件名、系统账户或原始 PowerShell 输出的诊断继续直接使用 `Debug.WriteLine`，不会持久化。

## 隐私边界

持久化日志不得记录订阅标题、完整 URL、HTML、PowerShell 脚本正文、原始标准输出、密钥、用户名、计算机名或设备标识。异常文本写入前会把用户目录、LocalAppData 和临时目录替换为逻辑占位符。

新增日志时，应优先记录数量、布尔状态、稳定错误分类和逻辑阶段。确需记录外部文本时，必须先确认其来源和敏感性，不能直接把用户输入或第三方响应作为结构化参数写入日志。
