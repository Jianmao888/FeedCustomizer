# 一键解除地区限制（技术原理说明）

本文档说明 FeedCustomizer 中“一键解除地区限制”功能的实现原理与实现细节。

## 概要

该功能通过在管理员权限下修改系统集成服务的区域策略 JSON 文件来解除第三方 Widgets 源在某些地区的显示限制。目标文件为：

%SystemRoot%\System32\IntegratedServicesRegionPolicySet.json

功能的目标是将策略项 “Third party feed is shown in Widgets.”（在实现中由特定 GUID 标识）中字段 defaultState 的值改为 "enabled"，以允许第三方源在小组件中显示。

## 面向用户的说明与警告

本节面向普通用户，非用于展示实现细节。该功能会以管理员权限修改位于系统目录的 JSON 系统策略文件，属于高风险操作。修改可能会被系统更新或 Windows 的完整性保护机制覆盖或回退，且不当修改可能导致系统相关集成功能异常。强烈建议：

- 在操作前手动备份原始文件；
- 仅在完全理解风险并在法律允许的前提下使用该功能；
- 若不确定，请不要启用此功能并寻求技术支持。

## 关键常量

- 策略文件名：IntegratedServicesRegionPolicySet.json
- 目标策略 GUID：{16d2b50e-fa7c-4bb1-ab17-01d766530b3b}
- 相关实现文件：FeedCustomizer/Core/Tools/RegionPolicyService.cs
- 调用的脚本：EnableThirdPartyWidgetFeed.ps1（由应用在提权环境中执行）

## 日志与诊断

脚本会把运行中的关键步骤和异常信息追加到一个 diagnostics 列表，发生错误时会把该诊断写到指定的错误文件路径（由应用传入）。脚本还会把 icacls / takeown 等命令的输出记录到诊断中，便于定位权限或文件访问问题。

## 安全性与回滚

- 操作需要管理员权限；脚本会尝试接管文件所有权并修改 ACL，这属于高权限系统改动。
- 脚本在修改前会保存原始 ACL 到临时文件（icacls /save），可用于人工恢复 ACL，但脚本自身并不自动还原该 ACL。
- 修改系统文件存在潜在风险，若操作失败或写回的 JSON 格式不正确，可能导致系统集成服务读取异常。建议在修改前备份原始 JSON 文件的完整副本以便回滚。

## 法律与合规

- 一键解除地区限制可能会涉及地区性服务或商店规则。请仅在遵守当地法律法规的前提下使用本功能。FeedCustomizer 仅提供技术手段，使用风险由用户自行承担。

## 参考代码

实现细节请参见：

- FeedCustomizer/Core/Tools/RegionPolicyService.cs
