<div align="center">

# FeedCustomizer

**自定义 Windows 小组件面板的源提供网站**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4.svg?logo=.net&logoColor=white)](#)
[![Windows App SDK](https://img.shields.io/badge/Windows%20App%20SDK-WinUI%203-blue.svg)](#)
[![Platform](https://img.shields.io/badge/Platform-Windows%2011-0078D6.svg?logo=windows&logoColor=white)](#)

<p align="center" style="display:flex;gap:16px;justify-content:center;align-items:center;">
  <a href="https://apps.microsoft.com/store/detail/9N0MB6RNWL85?cid=DevShareMCLPCS" title="从 Microsoft Store 获取">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="Document/Images/DownloadCard/get-it-on-microsoft-dark-zh.svg">
      <source media="(prefers-color-scheme: light)" srcset="Document/Images/DownloadCard/get-it-on-microsoft-light-zh.svg">
      <img src="Document/Images/DownloadCard/get-it-on-microsoft-light-zh.svg" height="55" alt="从 Microsoft Store 获取">
    </picture>
  </a>
  <a href="https://github.com/Jianmao888/FeedCustomizer/releases" title="从 Releases 获取（跳转到 Releases 页面）">
    <picture>
      <img src="Document/Images/DownloadCard/get-it-on-github.png" height="55" alt="从 Releases 获取">
    </picture>
  </a>
</p>
</div>

FeedCustomizer 是一款面向 Windows 11 的开源工具，允许你为系统小组件面板自定义源提供网站，把小组件内容换成自己想要的 RSS/网页源。

> 项目仍处于早期阶段，欢迎反馈与贡献。

## ✨ 功能特性

- **源管理**：添加、编辑、删除小组件源，支持自定义名称、网址与描述
- **图标支持**：自动解析并下载网站图标（favicon），也可选择本地自定义图片
- **一键启用/关闭**：随时启用或停用自定义源提供程序
- **外观个性化**：支持浅色 / 深色 / 跟随系统主题，以及 Mica / Mica Alt / Acrylic 背景材质
- **多语言**：内置简体中文与英语
- **捐献者版**：支持 Microsoft Store 加载项，识别捐献者身份
- **隐私友好**：不主动收集任何个人信息（详见[隐私政策](Document/PrivacyPolicy.md)）
- **自动获取开发者模式**：应用可在需要时自动检测并提示开启开发者模式，简化原生源提供程序的部署流程（可能需要管理员权限）。
- **一键解除地区限制**：在受地区限制的系统/商店场景下，提供一键解除地区限制的设置入口，帮助获取受限资源或功能（请根据法律法规合理使用）。

## 🖼 预览

<div style="display:flex;flex-direction:column;align-items:left;gap:8px;">
  <div style="display:flex;gap:8px;justify-content:left;flex-wrap:nowrap;">
    <img src="Document/Images/Preview/小组件面板示例-抖音.png" alt="截图1" style="width:48%;height:auto;object-fit:cover;" />
    <img src="Document/Images/Preview/小组件面板示例-Nasa.png" alt="截图2" style="width:48%;height:auto;object-fit:cover;" />
  </div>
  <div style="display:flex;gap:8px;justify-content:left;flex-wrap:nowrap;">
    <img src="Document/Images/Preview/应用截图-首页.png" alt="截图3" style="width:32%;height:auto;object-fit:cover;" />
    <img src="Document/Images/Preview/应用截图-编辑页面.png" alt="截图4" style="width:32%;height:auto;object-fit:cover;" />
    <img src="Document/Images/Preview/应用截图-设置页面.png" alt="截图5" style="width:32%;height:auto;object-fit:cover;" />
  </div>
</div>

## 📋 系统要求

- **操作系统**：Windows 11 22H2（10.0.22621）及以上，推荐 24H2（10.0.26100）
- **架构**：x64 / ARM64
- **运行环境**：Windows App SDK（MSIX 包已携带依赖，无需手动安装）

## 📦 安装

1. 从 [Releases](https://gitee.com/jianmao888/FeedCustomizer/releases) 下载最新的 MSIX/APPX 安装包
2. 双击安装包，按提示完成安装
3. 打开应用，添加你想在小组件面板中展示的源

> 若以源码方式运行，请参考下方「构建」一节。

## 🚀 使用说明

1. 启动 FeedCustomizer，开启「启用自定义源」。当需要部署原生源提供程序或额外权限时，应用会自动检测并尝试开启开发者模式（可能会提示 UAC 或要求管理员权限）。
2. 若遇到系统地区限制，可在「设置」中使用“解除地区限制”功能尝试解除受限功能（Beta）。
3. 点击「添加」新建源，填入网站地址与名称
4. 应用会自动抓取网站图标，你也可以手动选择图片
5. 点击「应用」保存，Windows 小组件面板即可显示自定义源

## 🔨 构建

### 环境要求

- Windows 11（满足 AppxManifest 中的 MinVersion）
- Visual Studio 2026（含 Windows App SDK 工作负载）
- .NET 10 SDK

### 构建步骤

1. 克隆仓库：
   ```bash
   git clone https://gitee.com/jianmao888/FeedCustomizer.git
   ```
2. 使用 Visual Studio 打开 `FeedCustomizer.slnx`
3. 恢复解决方案
4. 生成 FeedCustomizer 项目（WinUI 3 主应用）（注意：不要生成解决方案，否则会编译出错）
5. 部署或打包：使用 Visual Studio 的「部署」功能，或手动创建并安装 MSIX 包

## 🗂️ 项目结构

```text
FeedCustomizer/
├── FeedCustomizer/   # WinUI 3 主应用（界面、源管理、设置）
│   ├── Core/         # 核心服务与工具
│   ├── Pages/        # 页面（主页、添加/编辑源、设置）
│   ├── ViewModels/   # 视图模型
│   ├── Models/       # 数据模型
│   └── Resources/    # 资源与帮助文档
└── FeedProvider/     # 原生 AOT 的 COM 源提供程序
```

## 🛠️ 技术栈

- **语言 / 框架**：C#、.NET 10、WinUI 3（Windows App SDK）
- **打包**：MSIX
- **源提供程序**：Windows App SDK Widgets / Feed Provider（原生 AOT COM）

## 🔒 隐私

FeedCustomizer 不会主动收集任何个人信息，也不会向开发者所属的服务器发送信息。应用需要联网下载网站图标并检查捐献者版购买状态。详见[隐私政策](Document/PrivacyPolicy.md)。

## 📄 许可证

本项目依据 [MIT 许可协议](LICENSE) 开源。

### 👉 UWP版本

[由惜忆想睡觉制作](https://github.com/Furry-Xiyi/FeedCustomizer)

商店下载
<p align="left">
  <a href="https://apps.microsoft.com/detail/9nvqxzgpnp2m">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="Document/Images/DownloadCard/get-it-on-microsoft-dark-zh.svg">
      <source media="(prefers-color-scheme: light)" srcset="Document/Images/DownloadCard/get-it-on-microsoft-light-zh.svg">
      <img src="Document/Images/DownloadCard/get-it-on-microsoft-light-zh.svg" width="200" alt="从 Microsoft Store 获取">
    </picture>
  </a>
</p>
