# 一键解除地区限制（技术原理说明）

本文档说明 FeedCustomizer 中“一键解除地区限制”功能的实现原理与实现细节。

## 概要

该功能通过在管理员权限下修改系统集成服务的区域策略 JSON 文件来解除第三方 Widgets 源在某些地区的显示限制。目标文件为：

%SystemRoot%\System32\IntegratedServicesRegionPolicySet.json

功能的目标是将策略项 “Third party feed is shown in Widgets.”（在实现中由特定 GUID 标识）中字段 defaultState 的值改为 "enabled"，以允许第三方源在小组件中显示。

脚本以原始字节读取文件，JSON 解析仅用于检查。实际写回只替换目标 `defaultState` 的 `disabled` 值字节，保留其他所有字节，包括 UTF-8 BOM、缩进、换行、字段顺序、地区数组和末尾换行；不再全量序列化 JSON。目标已启用时不写入文件，也不调整权限。

目前支持 UTF-8 有／无 BOM，并要求目标 `guid` 后紧邻 `defaultState`，两者之间只有空白和逗号。目标缺失、重复、状态未知、定位有歧义、编码或 JSON 无效时明确失败，不猜测修改位置。写入前检查原文件是否变化，写入后核验字节；这些检查确认配置修改结果，系统功能是否生效仍需重启小组件或重新登录后验证。

## 面向用户的说明与警告

本节面向普通用户，非用于展示实现细节。该功能会以管理员权限修改位于系统目录的 JSON 系统策略文件，属于高风险操作。修改可能会被系统更新或 Windows 的完整性保护机制覆盖或回退，且不当修改可能导致系统相关集成功能异常。强烈建议：

- 在操作前手动备份原始文件；
- 仅在完全理解风险并在法律允许的前提下使用该功能；
- 若不确定，请不要启用此功能并寻求技术支持。

## 关键常量

- 策略文件名：IntegratedServicesRegionPolicySet.json
- 目标策略 GUID：{16d2b50e-fa7c-4bb1-ab17-01d766530b3b}
- 业务入口：FeedCustomizer/Core/Tools/RegionTools.cs
- 脚本实现：FeedCustomizer/Core/Infrastructure/PowerShell/RegionPolicyPowerShellAdapter.cs
- 调用的脚本：EnableThirdPartyWidgetFeed.ps1（由应用在提权环境中执行）

## 日志与诊断

脚本会把运行中的关键步骤和异常信息追加到一个 diagnostics 列表，发生错误时会把该诊断写到指定的错误文件路径（由应用传入）。脚本还会把 icacls / takeown 等命令的输出记录到诊断中，便于定位权限或文件访问问题。

## 安全性与回滚

- 操作需要管理员权限；脚本会尝试接管文件所有权并修改 ACL，这属于高权限系统改动。
- 脚本在修改前会保存原始 ACL 到临时文件（icacls /save），并在 finally 中尝试恢复原所有者和 ACL。
- 修改系统文件存在潜在风险，若操作失败或写回的 JSON 格式不正确，可能导致系统集成服务读取异常。建议在修改前备份原始 JSON 文件的完整副本以便回滚。

## 法律与合规

- 一键解除地区限制可能会涉及地区性服务或商店规则。请仅在遵守当地法律法规的前提下使用本功能。FeedCustomizer 仅提供技术手段，使用风险由用户自行承担。

## 参考代码

实现细节请参见：

- FeedCustomizer/Core/Tools/RegionTools.cs
- FeedCustomizer/Core/Infrastructure/PowerShell/RegionPolicyPowerShellAdapter.cs
