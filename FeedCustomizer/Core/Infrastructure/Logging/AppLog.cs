using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Threading;

namespace FeedCustomizer.Core.Infrastructure.Logging;

/// <summary>
/// 面向应用代码的日志接口。调用方只描述事件，不接触日志文件、滚动策略或 Serilog 全局配置。
/// </summary>
internal interface IAppLog
{
    void Debug(string messageTemplate, params object?[] propertyValues);

    void Information(string messageTemplate, params object?[] propertyValues);

    void Warning(string messageTemplate, params object?[] propertyValues);

    void Warning(Exception exception, string messageTemplate, params object?[] propertyValues);

    void Error(string messageTemplate, params object?[] propertyValues);

    void Error(Exception exception, string messageTemplate, params object?[] propertyValues);

    void Fatal(Exception exception, string messageTemplate, params object?[] propertyValues);
}

/// <summary>
/// 应用进程唯一的日志生命周期入口。初始化后配置保持不变，业务类只能取得带来源信息的只读记录器。
/// </summary>
internal static class AppLog
{
    private static readonly object LifecycleLock = new();
    private static int _initialized;
    private static int _closed;

    static AppLog()
    {
        // 文件系统尚不可用时仍保留调试输出，确保日志初始化本身的错误可在 VS 中看到。
        Log.Logger = CreateDebugLogger("尚未初始化");
    }

    /// <summary>本次进程启动的短标识，用于把同一轮启动中的跨层事件关联起来。</summary>
    internal static string SessionId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// 初始化文件与调试输出。失败时降级为仅调试输出，不能阻止应用启动。
    /// </summary>
    internal static void Initialize()
    {
        lock (LifecycleLock)
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return;
            }

            SelfLog.Enable(message => System.Diagnostics.Debug.WriteLine($"[Serilog] {message}"));

            try
            {
                string logPath = AppLogConfiguration.GetRollingFilePath();
                ILogger logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.WithProperty("SessionId", SessionId)
                    .WriteTo.Debug(outputTemplate: AppLogConfiguration.OutputTemplate)
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        fileSizeLimitBytes: AppLogConfiguration.FileSizeLimitBytes,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: AppLogConfiguration.RetainedFileCountLimit,
                        retainedFileTimeLimit: AppLogConfiguration.RetainedFileTimeLimit,
                        buffered: false,
                        shared: false,
                        outputTemplate: AppLogConfiguration.OutputTemplate)
                    .CreateLogger();

                ILogger previous = Log.Logger;
                Log.Logger = logger;
                (previous as IDisposable)?.Dispose();
                Volatile.Write(ref _initialized, 1);

                For(nameof(AppLog)).Information(
                    "日志系统初始化完成，保留天数={RetainedDays}，最多文件数={RetainedFileCount}，单文件上限字节={FileSizeLimitBytes}",
                    AppLogConfiguration.RetainedFileTimeLimit.Days,
                    AppLogConfiguration.RetainedFileCountLimit,
                    AppLogConfiguration.FileSizeLimitBytes);
                DeleteLegacyRegionPolicyLog();
            }
            catch (Exception ex)
            {
                // 日志是诊断能力而不是业务前置条件；失败时只写 Debug，不改变应用原有控制流。
                System.Diagnostics.Debug.WriteLine($"初始化文件日志失败：{LogPrivacy.RedactException(ex)}");
            }
        }
    }

    /// <summary>创建带有调用类型名称的记录器，便于按组件筛选日志。</summary>
    internal static IAppLog For<T>()
    {
        return new SerilogAppLog(typeof(T).FullName ?? typeof(T).Name);
    }

    /// <summary>为静态类型或明确的逻辑组件创建记录器。</summary>
    internal static IAppLog For(string sourceContext)
    {
        return new SerilogAppLog(sourceContext);
    }

    /// <summary>
    /// 在正常退出时同步刷新文件。方法可重复调用，后续异常回调不会再次关闭同一个 Sink。
    /// </summary>
    internal static void CloseAndFlush()
    {
        lock (LifecycleLock)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            if (Volatile.Read(ref _initialized) != 0)
            {
                For(nameof(AppLog)).Information("日志会话正常结束");
            }

            Log.CloseAndFlush();
            SelfLog.Disable();
        }
    }

    private static ILogger CreateDebugLogger(string sessionId)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("SessionId", sessionId)
            .WriteTo.Debug(outputTemplate: AppLogConfiguration.OutputTemplate)
            .CreateLogger();
    }

    private static void DeleteLegacyRegionPolicyLog()
    {
        try
        {
            if (AppLogConfiguration.DeleteLegacyRegionPolicyLog())
            {
                For(nameof(AppLog)).Information("已删除旧版独立地区策略诊断文件，后续诊断统一写入应用日志");
            }
        }
        catch (Exception ex)
        {
            // 遗留诊断清理失败不应破坏新日志初始化；保留警告供下次启动继续尝试。
            For(nameof(AppLog)).Warning(ex, "删除旧版独立地区策略诊断文件失败");
        }
    }
}

/// <summary>
/// 每次写入时从 Serilog 全局入口取得当前 Logger，避免类型在文件日志初始化前加载后永久持有旧实例。
/// </summary>
internal sealed class SerilogAppLog(string sourceContext) : IAppLog
{
    public void Debug(string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Debug, null, messageTemplate, propertyValues);
    }

    public void Information(string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Information, null, messageTemplate, propertyValues);
    }

    public void Warning(string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Warning, null, messageTemplate, propertyValues);
    }

    public void Warning(Exception exception, string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Warning, exception, messageTemplate, propertyValues);
    }

    public void Error(string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Error, null, messageTemplate, propertyValues);
    }

    public void Error(Exception exception, string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Error, exception, messageTemplate, propertyValues);
    }

    public void Fatal(Exception exception, string messageTemplate, params object?[] propertyValues)
    {
        Write(LogEventLevel.Fatal, exception, messageTemplate, propertyValues);
    }

    private void Write(
        LogEventLevel level,
        Exception? exception,
        string messageTemplate,
        object?[] propertyValues)
    {
        ILogger logger = Log.ForContext("SourceContext", sourceContext);
        if (exception is null)
        {
            logger.Write(level, messageTemplate, propertyValues);
            return;
        }

        // 不把 Exception 直接交给 Sink，防止堆栈中的用户目录原样落盘；脱敏文本仍保留类型和调用栈。
        var values = new List<object?>(propertyValues)
        {
            LogPrivacy.RedactException(exception)
        };
        logger.Write(level, messageTemplate + "；异常详情={ExceptionDetails}", values.ToArray());
    }
}
