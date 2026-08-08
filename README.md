# Windows Stacks

把 macOS 桌面的「叠放 (Stacks)」体验搬到 Windows。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 这是什么

Windows Stacks 是一个轻量桌面工具，能把杂乱的桌面文件**按类型 / 日期自动分组**成一个个可展开的「叠放堆」。点击堆展开，单击选中文件、双击打开——所有操作都在原生桌面之上叠加完成，**绝不动你的真实文件**。

## 功能特性

- 🗂 **智能分组**：按文件类型（图片 / 视频 / 文档 / 代码 …）或日期（今天 / 本周 / 本月 …）自动归类
- 👆 **单击选中 / 双击打开**：和 macOS 一致的交互手感
- 🖱 **拖拽排列**：叠放堆可自由拖动，松手自动吸附到网格
- 🎴 **网格 / 扇形**两种展开布局
- ⚡ **秒开**：图标异步加载，启动不卡顿
- 🚀 **开机自启**：注册表一键启用
- 🪟 **原生桌面共存**：原生图标照常可点，叠放层只在堆上拦截点击

## 安装

1. 到 [Releases](../../releases) 页面下载 `Stacks.exe`
2. 双击运行即可——**无需安装**、无外部依赖（.NET 6 runtime 已内嵌进单文件 exe）

> 调试模式：`Stacks.exe --debug` 不隐藏原生桌面图标，方便排查问题。

## 构建（开发者）

环境要求：.NET 6 SDK + Windows 10 / 11

```bash
git clone https://github.com/cjingwei6-hub/Windows-Stacks.git
cd Windows-Stacks
dotnet publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -o ./publish
# 产物：./publish/Stacks.exe
```

## 技术栈

- **语言 / 框架**：C# / .NET 6 / WPF（GPU 硬件加速）
- **架构**：全屏透明 overlay 自绘 + 原生桌面图标层共存，通过 Win32 `WM_NCHITTEST` 消息实现点击穿透
- **分组引擎**：三层判定（扩展名白名单 → PerceivedType → 启发式兜底），覆盖 130+ 扩展名

## 许可证

[MIT](LICENSE) © cjingwei6  //https://windows-stacks.pages.dev/#features
