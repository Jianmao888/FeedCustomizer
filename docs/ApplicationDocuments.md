# 应用文档维护说明

应用随包分发的 HTML 文档统一位于 `FeedCustomizer/Documents`。该目录不会被 Provider 部署模板枚举，因而不会再复制到 Provider 私有工作目录或真实注册目录。

运行时唯一文档副本位于应用私有数据目录的 `Local/Documents`。安装目录受 Windows 保护，只作为不可变同步源；页面和 ViewModel 不直接读取或复制文档。

## 版本与自愈

- `ApplicationDocumentService` 串行协调启动维护与用户打开请求。
- 应用包版本或文档目录结构版本变化时，全量构建候选目录，验证必要入口后再替换私有文档目录。
- 同一版本中，如果用户手动删除了文档入口，打开请求会从安装目录重建完整目录。
- 目前不计算内容哈希。同版本仅保证入口存在；应用更新必定全量覆盖，从而保证新版本内容一致。
- 新增隐私声明、开源许可等文档时，在 `ApplicationDocumentCatalog` 注册受控相对路径，不要新增只服务于单个文档的静态工具类。
- 开源软件许可声明位于 `FeedCustomizer/Documents/OpenSourceLicenses`；设置页通过与帮助文档相同的文档服务准备并由系统默认关联程序打开。

## 旧版副本清理

旧版本曾把 `HelpDoc` 同时复制到 Provider 私有工作目录和真实 `%LocalAppData%\FeedCustomProvider`。新结构只在确认应用更新、且新文档目录已经同步成功后清理这些遗留副本。

首次采用本结构时，旧私有 `HelpDoc` 是否存在用于识别升级安装；后续版本使用文档状态中的包版本判断。真实 LocalAppData 的清理由受控 PowerShell 适配器执行：清理成功后永久标记完成；失败时同一版本不重复执行，只在下一次应用更新后再尝试一次。全新安装、普通同版本启动和缺失文件自愈都不会启动 PowerShell。
