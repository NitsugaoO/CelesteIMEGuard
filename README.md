# CelesteIMEGuard

![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)
![Everest](https://img.shields.io/badge/Everest-1.0+-blue.svg)

## Don't Read Me
本项目除了这个部分都是 AI (DSFv4) 写（和上传）的。

我在加了 Miaonet 打 Celeste 之后遇到了这样的问题：卡输入法。准确来说就是中文输入法启动游戏，会在操作的时候出现输入候选框。我立刻想到了 IMBlocker ([MC 百科链接](https://www.mcmod.cn/class/3358.html)) 这个 Minecraft Mod，但查了一圈发现好像 Celeste 还没有这样的模组。

怎么会这样！然后启动 pi，一查发现 Miaonet 原来是有做这个功能的，但是（至少在我这里）要打开过一次聊天框之后才会有效果。然后就产生了这个模组。

我总感觉很奇怪...这种问题居然还没有修复？感觉更像是我这边的个例情况。总之 token 的钱都花了问题也解决了就发出来吧。

当前环境：Miaonet alpha 0.5.2，微信输入法。测试启动游戏后、打开聊天框一次前不再出现卡输入法的情况，在聊天框里中文输入法运作正常。

## 概述
一个 "IMBlocker" 风格的 Celeste (Everest) mod：**游戏运行时自动摘除系统输入法 (IME)，进聊天框时自动恢复**，解决 Windows 中文输入法在 Celeste 启动后弹出候选框/干扰操作的问题。

> 灵感来自 Minecraft 的 [IMBlocker](https://github.com/reserveword/IMBlocker)（仅借鉴思路，代码为原创）。

> 📚 **开发者/回顾入口**：完整实现路径、踩坑记录、技术决策与未来改动指引见
> [`docs/ARCHIVE.md`](docs/ARCHIVE.md)（根因分析、SDL/Everest 机制详解在 `docs/research/`）。
>
> 注：本地研究用的反编译摘录（`docs/reference/`）**不随本仓库分发**（版权敏感）。

## 解决什么问题

在 Windows 上，系统输入法（如微软拼音）是全局的。Celeste 启动后——在**任何聊天框被打开过之前**——Everest 和 FNA/SDL 从未调用过 `SDL_StartTextInput`/`SDL_StopTextInput`，因此 SDL 内部的 IME 初始化 (`IMM_Init → IMM_Disable → ImmAssociateContext(hwnd, NULL)`) **从未执行过**，系统 IME 一直挂在游戏窗口上。此时按 Shift 或某些键会弹出输入法候选框，干扰操作。

而当你开过一次聊天框（Everest 订阅/退订 `TextInput.OnInput`）后，SDL 的 IME 状态被"理顺"，之后才正常——这就是"刚启动有问题，打过字后正常"现象的根本原因。

## 修复原理

基于 SDL 2.28（Celeste 自带版本）自身机制，见 `SDL_windowsevents.c`：

- `SDL_StopTextInput` → `WIN_StopTextInput` → `IME_Init`（首次初始化，内部立即 `IME_Disable`）→ `ImmAssociateContext(hwnd, NULL)` 摘除 IME
- `SDL_StartTextInput` → `IME_Enable` → `ImmAssociateContext(hwnd, ime_himc)` 重新关联 IME

本 mod 在游戏窗口创建后**主动调用一次 `StopTextInput`**，触发 SDL 完成初始化并摘除 IME（关键修复）。之后每帧守卫：

- SDL 文本输入激活中 **且** 无任何文本框订阅 `TextInput.OnInput` → 再次 `StopTextInput`（保险网）
- 有文本框订阅（聊天打开）→ 不干预，由 Everest 的 `StartTextInput` 自动恢复 IME，中文合成输入正常

不依赖 imm32 P/Invoke，与 SDL/Everest 状态天然同步。

## 文件结构

```
CelesteIMEGuard/
├── README.md              # 项目总览
├── LICENSE                # MIT
├── .gitignore
├── everest.yaml           # mod 清单
├── src/                   # 源码工程 (net8.0)
│   ├── CelesteIMEGuard.csproj
│   └── IMEGuardModule.cs
├── docs/                  # 开发文档（ARCHIVE / research）
└── dist/                  # 发布产物（不入仓库，走 GitHub Release）
```

打包结构（安装用 zip）：

```
CelesteIMEGuard.zip
├── everest.yaml              # mod 清单
└── Code/CelesteIMEGuard.dll  # 编译产物 (net8.0)
```

## 安装

### 方式一：从 Release 下载（推荐）

到 [Releases](https://github.com/wooyun71/CelesteIMEGuard/releases) 下载最新的 `CelesteIMEGuard.zip`。

### 方式二：手动打包

```bash
# 编译后按以下结构打 zip
cd src && dotnet build -p:CelesteDir="<你的 Celeste 目录>"
# zip 根目录需直接含 everest.yaml 与 Code/ 文件夹
```

把 `CelesteIMEGuard.zip` 放入 Celeste 安装目录的 `Mods/` 文件夹，重启游戏即可。

> **前置要求**：需要先安装 [Everest](https://everestapi.github.io/)（Celeste mod 加载器）。
> 推荐用 [Olympus](https://github.com/EverestAPI/Olympus) 管理 mod（可 1 键安装本 mod 的 zip）。

## 卸载

删除 `Mods/CelesteIMEGuard.zip` 即可，无残留状态（IME 摘除是运行时行为，游戏退出即重置）。

## 开发

```bash
# 编译（CelesteDir 指向 Celeste 安装目录；路径含空格括号，务必走环境变量）
export CelesteDir='C:\Program Files (x86)\Steam\steamapps\common\Celeste\'
dotnet build src/CelesteIMEGuard/CelesteIMEGuard.csproj -p:CelesteDir="$CelesteDir"
# 产物: src/CelesteIMEGuard/bin/Debug/CelesteIMEGuard.dll → 按 dist/ 结构打 zip
```

引用：`Celeste.dll`（Everest 补丁版，含全部 API）、`FNA.dll`、`MMHOOK_Celeste.dll`。
**注意**：不要引用 `Celeste.Mod.mm.dll`（会 CS0433 类型歧义）；类型名用 `global::` 前缀。

## 验证日志（IMEGuard tag，游戏 log.txt）

```
Startup StopTextInput issued (SDL IME detached from window). IsTextInputActive=False  # 启动修复
TextInput.OnInput subscribers=1 IsTextInputActive=True   # 打开聊天 → IME 恢复
TextInput.OnInput subscribers=0 IsTextInputActive=False  # 关闭聊天 → IME 摘除
```

## 参考与致谢

- 机制借鉴自 Minecraft mod [IMBlocker](https://github.com/reserveword/IMBlocker) 的 IME 自动开关思路（仅思路，无代码复用）
- SDL 2.28 Windows IME 实现：`src/video/windows/SDL_windowskeyboard.c`（`IME_Init`/`IME_Enable`/`IME_Disable`）
- Everest `Celeste.Mod.TextInput`：订阅 `OnInput` 自动 `StartTextInput`/`StopTextInput`
- [Everest](https://github.com/EverestAPI/Everest)（MIT）与 [FNA](https://fna-xna.github.io/) 为本 mod 提供运行时 API

## 免责声明

- 本 mod 是第三方社区作品，与 Celeste 开发商 **Extremely OK Games** 无关，未获其官方认可或赞助。
- 通过反射访问 Everest 内部字段（`Celeste.Mod.TextInput._OnInput`）以判断文本框状态；Everest 升级后若内部结构变化，本 mod 会自动降级为不干预（保守安全），届时请更新本 mod。

## License

[MIT](LICENSE) © 2026 wooyun71
