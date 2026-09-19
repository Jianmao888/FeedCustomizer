# 开源软件许可声明

感谢您使用 FeedCustomizer。

FeedCustomizer 使用了若干由开源社区、Microsoft 及其他贡献者开发的软件组件。我们尊重各组件的著作权和许可条件，并在本页面列出主要组件、版本、许可类型及其来源，供您查阅。

FeedCustomizer 自身的源代码依据 [MIT 许可协议](../LICENSE) 发布。下列第三方组件不属于 FeedCustomizer 的原创代码，其许可条件独立于 FeedCustomizer 的许可条件。

## 第三方组件

### MIT License

以下组件依据 MIT License 发布：

| 组件 | 版本 | 来源 |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| CommunityToolkit.Common | 8.2.1 | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.3.260402-preview2 | [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows) |
| CommunityToolkit.WinUI.Extensions | 8.3.260402-preview2 | [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows) |
| CommunityToolkit.WinUI.Helpers | 8.3.260402-preview2 | [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows) |
| CommunityToolkit.WinUI.Triggers | 8.3.260402-preview2 | [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows) |
| Microsoft.DotNet.ILCompiler | 10.0.12 | [.NET](https://github.com/dotnet/dotnet) |
| Microsoft.NET.ILLink.Tasks | 10.0.12 | [.NET](https://github.com/dotnet/dotnet) |

### Apache License 2.0

以下 Serilog 组件依据 Apache License 2.0 发布：

| 组件 | 版本 | 来源 |
| --- | --- | --- |
| Serilog | 4.4.0 | [serilog/serilog](https://github.com/serilog/serilog) |
| Serilog.Sinks.Debug | 3.0.0 | [serilog/serilog-sinks-debug](https://github.com/serilog/serilog-sinks-debug) |
| Serilog.Sinks.File | 7.0.0 | [serilog/serilog-sinks-file](https://github.com/serilog/serilog-sinks-file) |

### BSD 3-Clause License

| 组件 | 版本 | 来源 |
| --- | --- | --- |
| Microsoft.Web.WebView2 | 1.0.3719.77 | [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) |

WebView2 SDK 附带的声明还包含以下 BSD 3-Clause 组件：`Antlr3.Runtime 3.5.2-rc1` 和 `StringTemplate4 4.0.9-rc1`。

### .NET 运行时及其第三方组件

FeedCustomizer 使用 .NET 10 运行时及其原生 AOT 支持。相关运行时和工具包包含 Microsoft 发布的第三方组件声明，例如 ASP.NET、zlib-ng、OpenTelemetry .NET、Mono、Brotli、Json.NET、LLVM、xxHash、MessagePack-CSharp、lz4net、RapidJSON、DirectX Math Library、musl、mimalloc、fmtlib/fmt、MsQuic 等。

这些组件的完整名称、版权信息和许可证文本以对应 .NET 发行包附带的 `THIRD-PARTY-NOTICES.TXT` 为准：[dotnet/dotnet](https://github.com/dotnet/dotnet)。

## Microsoft 软件许可组件

FeedCustomizer 使用 Windows App SDK 和 Windows SDK 提供 Windows 小组件、WinUI 及相关系统能力。以下组件受其随包提供的 Microsoft Software License Terms 约束，不应被理解为依据 FeedCustomizer 的 MIT License 发布：

| 组件 | 版本 |
| --- | --- |
| Microsoft.WindowsAppSDK.Base | 2.0.4 |
| Microsoft.WindowsAppSDK.Foundation | 2.3.9 |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.6 |
| Microsoft.WindowsAppSDK.Runtime | 2.4.0 |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 |
| Microsoft.WindowsAppSDK.WinUI | 2.3.6 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 / 10.0.26100.4654 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 |

详细条款请参阅：

- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK)
- [Microsoft Windows App 软件许可条款](https://learn.microsoft.com/legal/windows-app/windows-app-license-terms)
- [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/)

Windows App SDK、Windows SDK 和 WebView2 的发行包可能包含其他第三方组件。发布版本随附的许可证和 NOTICE 文件优先于本页面的摘要内容。

## 主要许可证文本

以下内容用于展示各许可证的主要条款；具体版权行、NOTICE 内容及完整许可证文本，以对应组件随附的原始许可证文件为准。

### MIT License

```text
MIT License

Copyright (c) the respective copyright holders

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0

Serilog 组件的完整许可证文本请参阅 [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0)。Apache License 2.0 要求保留许可证文本及相关版权、NOTICE 信息；如果对组件进行了修改，应明确标注修改内容。

### BSD 3-Clause License

```text
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

## 版权与免责声明

第三方组件由其各自的版权所有者和贡献者提供。除适用法律另有规定外，第三方组件均按其原始许可证中的“按现状”条款提供，FeedCustomizer 作者不对第三方组件作出额外保证。

本页面中的产品名称、项目名称、商标和标识归其各自所有者所有。提及这些名称不表示相关所有者对 FeedCustomizer 的认可或赞助。

最后更新：2026-09-19
