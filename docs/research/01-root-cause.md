# 根因分析：Celeste 启动时输入法（IME）不自动关闭

> 归档日期：2026-09-07 ｜ 场景：Windows + 中文输入法（微软拼音）+ Celeste(Everest) + MiaoNet 聊天

## 一、问题现象（用户原话归纳）

- 刚启动游戏、**还没打开过任何聊天框之前**，输入法没有自动关闭。
- 表现：按 Shift 或某些键会**弹出输入法候选框**，很烦人。
- **一旦打开过一次输入框打字之后，就完全正常了**（"打开输入框打字之后就很正常"）。
- 用户打中文的场景：MiaoNet alpha 0.5.2 的聊天框（Celeste 中文社区重构版联机 mod）。

## 二、关键背景：Celeste 的输入架构

```
按键 → SDL2 事件 → FNA(TextInputEXT) → Everest(Celeste.Mod.TextInput) → mod 文本框
                                        ↘ Monocle MInput.Keyboard → 游戏操作（原始按键，不经 IME）
```

| 层 | 职责 | 关键点 |
|---|---|---|
| SDL2 2.28.5 | 窗口 + IME 底层 | `SDL_StartTextInput/StopTextInput` 控制 IME |
| FNA | XNA 重实现 | `TextInputEXT.StartTextInput` 等 = 直接转发 SDL 函数（无包装） |
| Everest | mod 加载器 | `Celeste.Mod.TextInput` 提供 `OnInput` 事件，**自动**开关 IME |
| MiaoNet | 聊天 mod | 打开聊天订阅 `OnInput`，关闭退订 |

## 三、Everest 的"被动" IME 开关（机制）

`Celeste.Mod.TextInput`（反编译自本机 Celeste.Mod.mm.dll）的核心：

```csharp
private static void CheckTextStatus()
{
    if (订阅者 0->1 且 !IsTextInputActive())
        TextInputEXT.StartTextInput();   // 进聊天 → 开 IME
    else if (订阅者 1->0 且 IsTextInputActive())
        TextInputEXT.StopTextInput();    // 离开 → 关 IME
}
```

- `OnInput` 的 `add/remove` 都会调用 `CheckTextStatus()`。
- **这是"被动/反应式"开关**：只有订阅者数量变化时才触发。
- 注释原文也说明：*"This event is in charge of managing `StartTextInput` and `StopTextInput` calls."*

## 四、真正的根因（决定性发现）

### 4.1 表面层：启动时无人触发 IME 初始化

启动时序：

```
Everest.Initialize()
  └→ TextInput.Initialize(game)   // 只订阅 TextInputEXT.TextInput 事件
       └→ CheckTextStatus()       // _OnInput 为空 → 什么都不做（不 Start 也不 Stop）
```

→ 启动后 **SDL 层 `IsTextInputActive() == false`**（实测日志证实），Everest/FNA **从未调用过**
`SDL_StartTextInput` 或 `SDL_StopTextInput`。

### 4.2 深层：SDL 2.28.5 的 IME 摘除逻辑"从未被执行"

读 SDL 2.28.5 源码 `src/video/windows/SDL_windowskeyboard.c`：

```c
// WIN_StopTextInput (~L218):  无条件 IME_Init + IME_Disable（没有"必须先 Start"的守卫）
// WIN_StartTextInput (~L198): IME_Init + IME_Enable

// IME_Init (~L375): 首次初始化，末尾【立即 IME_Disable】
//   videodata->ime_himc = ImmGetContext(hwnd);   // 拿默认 IMC
//   ...
//   IME_Disable(videodata, hwnd);

// IME_Enable (~L413):
//   if (ime_hwnd_current == ime_hwnd_main)
//       ImmAssociateContext(ime_hwnd_current, ime_himc);  // 关联 SDL 自己的 IMC

// IME_Disable (~L430):   ← 核心
//   if (ime_hwnd_current == ime_hwnd_main)
//       ImmAssociateContext(ime_hwnd_current, (HIMC)0);   // 摘除 IMC！
```

**根因一句话**：`ImmAssociateContext(hwnd, NULL)`（把系统 IME 从游戏窗口摘除）这段代码，只在
`IME_Init`/`IME_Disable` 里执行；而它们**只有 SDL 的 Start/StopTextInput 被调用过才会跑**。
启动时没人调 → SDL 内部 IME 状态机从未初始化 → IME 一直挂在窗口上 → 弹候选框。

### 4.3 为什么"开过一次聊天就正常"？

打开聊天 → Everest 订阅 `OnInput` → `StartTextInput` → SDL `IME_Init`（内部先 `IME_Disable` 摘除）
→ `IME_Enable` 关联。
关闭聊天 → 退订 → `StopTextInput` → `IME_Disable` 摘除。

**这一轮 Start/Stop 把 SDL 的 IME 状态机"初始化并理顺"了**，之后 IME 正确摘除。所以用户体感
"打过字后就正常"。

## 五、结论与修复方向

| 方案 | 说明 | 采用？ |
|---|---|---|
| A. 启动后主动调一次 `StopTextInput` | 触发 SDL `IME_Init`(内含 `IME_Disable`) → 摘除 IME。**用 SDL 自身机制，零 P/Invoke，与游戏状态天然同步** | ✅ **采用（本 mod v1.2 核心）** |
| B. 直接 P/Invoke `imm32.dll` `ImmAssociateContext(hwnd, NULL)` | IMBlocker 在 Minecraft(GLFW) 的做法。但 Celeste 用 SDL，SDL 自己会管 IMC，手动摘除可能与 SDL 状态机冲突 | ❌ 放弃（初版试过，改 A） |
| C. 改 Everest/MiaoNet 源码 | 侵入大，不通用 | ❌ |

## 六、验证日志（实测，游戏 log.txt 中 IMEGuard tag）

```
Startup StopTextInput issued (SDL IME detached from window). IsTextInputActive=False  # 启动修复生效
TextInput.OnInput subscribers=1 IsTextInputActive=True   # 打开聊天 → IME 恢复（Everest/SDL 自动）
TextInput.OnInput subscribers=0 IsTextInputActive=False  # 关闭聊天 → IME 摘除
```

用户实测结论：**"很好没有问题"** —— 启动不弹候选框、聊天中文正常、关闭后恢复。

## 七、相关文件

- 本 mod 实现：`../src/CelesteIMEGuard/IMEGuardModule.cs`
- Everest TextInput 反编译：`../reference/00-Everest-TextInput.cs`、`05-CelesteMod-TextInput-runtime.cs`
- SDL 2.28.5 源码（决定性证据）：`../reference/03-SDL2.28-windowskeyboard-IME.c`
- FNA 转发层：`../reference/06-FNA-FNAPlatform.cs`
