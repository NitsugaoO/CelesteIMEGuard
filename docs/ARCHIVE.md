# CelesteIMEGuard 总归档

> 归档日期：2026-09-07 ｜ 项目：Celeste (Everest) 的 IMBlocker 式输入法守卫 mod
> 状态：✅ 已实测通过（用户确认"很好没有问题"），已安装使用中

---

## 0. 项目是什么

一个 Celeste (Everest) mod：**游戏运行时自动从窗口摘除系统 IME（不弹候选框），
打开聊天框时自动恢复 IME（中文可正常输入）**。解决 Windows 中文输入法在 Celeste
启动后干扰游戏的问题。思路借鉴 Minecraft 的 [IMBlocker](https://github.com/reserveword/IMBlocker)。

## 1. 目录结构

```
~/Projects/CelesteIMEGuard/
├── README.md                    # 项目总览（对外）
├── everest.yaml                 # mod 清单（安装到 Mods/ 时随 zip 打包）
├── src/CelesteIMEGuard/
│   ├── CelesteIMEGuard.csproj   # net8.0 工程（引用 Celeste.dll/FNA.dll/MMHOOK_Celeste.dll）
│   └── IMEGuardModule.cs        # 全部逻辑（198 行，含日志）
├── docs/
│   ├── ARCHIVE.md               # ← 本文件：总归档
│   ├── research/
│   │   ├── 01-root-cause.md               # 根因分析（为什么启动时 IME 不关）
│   │   ├── 02-sdl-ime-mechanism.md        # SDL 2.28.5 IME 机制详解
│   │   └── 03-everest-textinput-architecture.md  # Everest 输入架构速查
│   └── reference/               # 精选反编译/源码摘录（带说明头）
│       ├── 00-Everest-TextInput.cs            (Everest OnInput 自动开关)
│       ├── 01-MiaoNet-InputBox.cs             (MiaoNet 聊天输入框)
│       ├── 02-MiaoNet-ChatComponent.cs        (聊天生命周期)
│       ├── 03-SDL2.28-windowskeyboard-IME.c   (SDL IME 源码=决定性证据)
│       ├── 04-MMHOOK-On.Monocle.Engine.cs     (On.* detour 签名)
│       ├── 05-CelesteMod-TextInput-runtime.cs (运行版 Celeste.dll 里的 TextInput)
│       └── 06-FNA-FNAPlatform.cs              (TextInputEXT→SDL 转发)
└── dist/                        # 发布产物
    ├── CelesteIMEGuard.zip      # 已安装到 Celeste\Mods\
    ├── Code/CelesteIMEGuard.dll # 编译产物 (net8.0, Debug)
    └── everest.yaml
```

## 2. 实现路径（时间线）

| 阶段 | 做了什么 | 产出 |
|---|---|---|
| 0 需求澄清 | 调研 IMBlocker(Minecraft) 效果；问清用户真实目标是 Miaonet 聊天自动开关 IME | — |
| 1 逆向 MiaoNet | 装 ilspycmd，反编译 MiaoNet 0.5.2 DLL（带 PDB 很干净） | ~/miaonet_decomp（后精简入 reference） |
| 2 逆向 Everest | 反编译 Celeste.Mod.mm.dll 找 TextInput/Events API | ~/everest_decomp |
| 3 逆向 FNA | 反编译 FNA.dll 确认 TextInputEXT→SDL 转发 | ~/fna_decomp |
| 4 根因定位 | 实测日志发现 `IsTextInputActive=False` 但用户仍跳候选框 → 怀疑 Win32 层 | — |
| 5 源码实证 | clone SDL 2.28.5 读 windowskeyboard.c，**找到决定性证据**（IME_Init 内含 IME_Disable） | /tmp/sdl228 |
| 6 方案定案 | 由"P/Invoke imm32 摘除"改为"启动调一次 StopTextInput 借 SDL 机制" | v1.2.0 |
| 7 实测验证 | 装进 Mods/ 启动游戏，日志 + 用户试玩确认三场景全过 | ✅ 完成 |

## 3. 踩过的坑（重要！）

### 坑1：类型歧义 CS0433 — EverestModule 同时在 Celeste.dll 和 Celeste.Mod.mm.dll
- **症状**：`error CS0433: 类型"EverestModule"同时存在于"Celeste.Mod.mm"和"Celeste"中`
- **原因**：Everest 的 mm 补丁把 API 同时编进了 Celeste.dll（运行视图）和 mm.dll。
- **解法**：csproj **只引用 `Celeste.dll`**，不引用 `Celeste.Mod.mm.dll`。

### 坑2：`Celeste.Mod.TextInput` 编译报"Celeste 中不存在 Mod"
- **症状**：`error CS0426/CS0117: 类型"Celeste"中不存在类型名"Mod"`
- **原因**：文件在 `namespace Celeste.Mod.IMEGuard` 内，裸写 `Celeste.Mod.TextInput`
  时 `Celeste` 被解析成类而非命名空间（还有 `Celeste.Celeste` 游戏主类干扰）。
- **解法**：一律写 `global::Celeste.Mod.TextInput`。

### 坑3：bash 传含空格+括号的 Windows 路径
- `-p:CelesteDir="C:\Program Files (x86)\..."` 在 bash 里引号解析炸（unexpected EOF）。
- **解法**：`export CelesteDir='...'` 环境变量 + `dotnet build -p:CelesteDir="$CelesteDir"`。

### 坑4：手搓 `new Hook(...)` 易错 → 用 MMHOOK 生成事件
- 第一版用 `new Hook(methodInfo, delegate)` 手写 detour，委托签名容易错。
- **解法**：改用 `On.Monocle.Engine.Update += (orig, self, gt) => {...}`（MMHOOK_Celeste.dll
  生成强类型事件，Everest 生态标准），签名自动匹配。

### 坑5：edit 工具不允许空 oldText
- 多段插入不能用空 oldText，整段重写用 write。

### 坑6：ilspycmd 导出单类型语法
- `ilspycmd -t Namespace.Type -o dir dll` 未必输出（有的版本只支持 `-p` 整工程或 `-l c` 列表）。
- 稳定做法：`-p -o dir` 整程序集反编译，再 find 目标 .cs；或 `-t` 时看实际输出文件名
  （常是 `Type.decompiled.cs` 而非 `Type.cs`）。

### 坑7：PowerShell Compress-Archive 会把已存在的同名 zip 再包一层
- dist 里出现过 `CelesteIMEGuard.zip` 内含另一个 `CelesteIMEGuard.zip` 条目。
- **解法**：先 `rm -f` 再压缩，或用 python zipfile 精确控制条目。

### 坑8：反编译乱码/编码
- 控制台 GBK 下 python 输出中文乱码（文件本身 UTF-8 正常）；头注释里 Windows 路径
  反斜杠在 python 字符串要转义（SyntaxWarning）。写文件用 UTF-8 编码。

## 4. 关键技术决策记录（含放弃的方案）

| 决策点 | 选项 | 结论 | 原因 |
|---|---|---|---|
| 修复手段 | A. 启动调 StopTextInput（SDL 机制）<br>B. P/Invoke imm32 ImmAssociateContext(NULL) | **A** | SDL 自己管 IMC；B 会与 SDL 状态机冲突，且初版实现复杂 |
| hook 方式 | A. `On.Monocle.Engine.Update` 事件<br>B. 裸 `new Hook` | **A** | 签名强类型、生态标准、不易错 |
| 判断"有无文本框" | A. 反射读 `TextInput._OnInput` 调用列表<br>B. 订阅探测（挂临时订阅） | **A** | B 会误触发 Everest 的 StartTextInput，有副作用 |
| 兜底时机 | 每帧 + 窗口 OnActivated | **两者都要** | 覆盖 alt-tab 回焦点场景 |
| 启动 Stop 时机 | Load 里立刻 / 延迟 N 帧 | **延迟 10 帧** | Load 时窗口可能未建、TextInput 未 Initialized |
| 调研材料归档 | 全量 vs 精选 | **精选**（本次归档） | 反编译树+SDL 源码近百 MB，只留关键摘录 |

## 5. 验证记录

游戏 log.txt（IMEGuard tag）关键行：
```
Startup StopTextInput issued (SDL IME detached from window). IsTextInputActive=False
TextInput.OnInput subscribers=1 IsTextInputActive=True    # 开聊天，IME 恢复
TextInput.OnInput subscribers=0 IsTextInputActive=False   # 关聊天，IME 摘除
```
用户实测：启动不弹候选框 ✓ / 聊天中文输入正常 ✓ / 关闭后恢复 ✓。无崩溃无报错。

## 6. 未来改动指引（新需求往哪改）

### 需求 A：聊天输入框内 / 开头命令时临时切英文输入法（类 IMBlocker 命令检测）
- 位置：参考 `InputBox.Update()` 已有 completion 逻辑；需在 TextInput 层判断当前文本
  是否以 `/` 开头，临时 Stop/Start。**注意**：SDL IME 中英切换不是标准 API，可能要 imm32
  `ImmSetOpenStatus` 或模拟按键，属"系统级操作"，先查可行性。

### 需求 B：候选框位置更精确跟随光标
- MiaoNet 已在 `InputBox.Render()` 用 `TextInputEXT.SetInputRectangle`；改这里即可。
- 本 mod 不管这个（只做开关），勿混。

### 需求 C：支持 Linux/macOS
- 本 mod 的 SDL 机制理论跨平台（SDL_Start/Stop 都有），但 Windows 摘除逻辑依赖
  `RuntimeInformation.IsOSPlatform(Windows)` 分支。Linux 需另研究 fcitx5 行为。

### 需求 D：Everest 升级后兼容性
- 若 `TextInput._OnInput` 字段改名/改公开 → 反射失败时本 mod 已保守返回（不影响游戏），
  但功能失效。升级后查 reference/00 对照，必要时改用新公开 API。

### 改完怎么重新发布
```bash
cd ~/Projects/CelesteIMEGuard
dotnet build src/CelesteIMEGuard/CelesteIMEGuard.csproj -p:CelesteDir="C:\Program Files (x86)\Steam\steamapps\common\Celeste\"
# 打 zip: everest.yaml + Code/CelesteIMEGuard.dll（见 dist/ 现成结构）
# 覆盖安装到 Celeste\Mods\CelesteIMEGuard.zip，重启游戏看 log.txt 的 IMEGuard 日志
```

## 7. 环境信息（便于复现）

| 项 | 值 |
|---|---|
| 游戏 | Celeste (Steam)，Everest 已装 |
| 运行时 | net8.0（Celeste.runtimeconfig.json: Microsoft.NETCore.App 8.0.0） |
| SDL2 | 2.28.5（Celeste\everest-lib\lib64-win-x64\SDL2.dll） |
| FNA | 20.x（含 TextInputEXT.TextEditing/SetInputRectangle，FNA 20.10+ 特性） |
| 关键 mod | MiaoNet 0.5.2-alpha（依赖 EverestCore、ChineseFontPack） |
| 工具 | dotnet SDK 8/10、ilspycmd 11、python、git |
| 游戏目录 | `C:\Program Files (x86)\Steam\steamapps\common\Celeste\` |
| 日志 | 游戏目录 `log.txt`（Everest 日志，含 IMEGuard tag） |

## 8. 相关外部链接

- IMBlocker（灵感来源）：https://github.com/reserveword/IMBlocker
- Everest：https://github.com/EverestAPI/Everest
- FNA TextInputEXT 文档：https://github.com/FNA-XNA/FNA/wiki/5:-FNA-Extensions#textinputext
- SDL 2.28.5 源码：https://github.com/libsdl-org/SDL/tree/release-2.28.5
