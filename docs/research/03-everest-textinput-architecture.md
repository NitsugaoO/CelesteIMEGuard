# Everest 输入/IME 相关架构速查

> 归档日期：2026-09-07 ｜ 基于本机 Celeste(Everest) 反编译（celeste-decomp 已随本项目重建为 reference 精选）

## 一、Celeste + Everest 的运行时架构

```
Celeste.exe (net8.0 apphost)
 ├─ Celeste.dll            ← 被 Everest "mm" 补丁改写：内含 Celeste.Mod.* API（587 类）
 ├─ Celeste.Mod.mm.dll     ← Everest 本体程序集（586 类，与 Celeste.dll 几乎同集）
 ├─ FNA.dll                ← XNA 重实现（内含 TextInputEXT / SDL2 绑定）
 ├─ MMHOOK_Celeste.dll     ← 运行时 detour 生成 API（On.* / IL.*）
 └─ Mods/*.zip             ← 各 mod（MiaoNet、CelesteIMEGuard…）
```

**重要**：`Celeste.dll` 与 `Celeste.Mod.mm.dll` 都含 `Celeste.Mod.EverestModule`/`TextInput` 等类型。
**mod 项目只应引用 `Celeste.dll`**（否则编译报 CS0433 类型歧义）。Everest 通过 AssemblyLoadContext
按需装配 mod 到 `Celeste.dll` 的视图。

## 二、输入链路

```
物理按键
  → SDL2: SDL_KEYDOWN/UP（原始按键，走 MInput.Keyboard → 游戏操作，不经 IME）
          SDL_TEXTINPUT/SDL_TEXTEDITING（IME 文本/合成，仅文本输入激活时）
  → FNA: TextInputEXT (Microsoft.Xna.Framework.Input)
          ├ TextInputEXT.TextInput / .TextEditing 事件
          ├ StartTextInput() / StopTextInput() / IsTextInputActive()
          └ SetInputRectangle(rect)   ← IME 候选框定位
  → Everest: Celeste.Mod.TextInput（对 mod 暴露的静态事件门面）
          ├ OnInput 事件（add/remove 自动 Start/Stop IME）
          └ GetClipboardText / SetClipboardText
  → mod 文本框（MiaoNet InputBox 等）订阅 OnInput
```

## 三、Everest 关键 API 速查（mod 开发用）

| API | 说明 |
|---|---|
| `EverestModule` | mod 基类。`Load()/Unload()/LoadContent()/Initialize()` 等虚方法 |
| `Everest.Events.*` | 生命周期事件：`Celeste.OnExiting`、`Level.OnLoadLevel`、`Input.OnInitialize`… |
| `On.Monocle.Engine.Update` | **每帧 hook**（MMHOOK 生成事件，`orig_Update(Engine, GameTime)`） |
| `On.Monocle.Engine.OnActivated/OnDeactivated` | 窗口焦点事件（失焦/重获焦点） |
| `Logger.Info/Warn/Error(tag, msg)` | 日志，写入游戏 `log.txt` |
| `TextInputEXT.*` | FNA 层 IME 控制（本 mod 直接使用） |
| `Celeste.Celeste.Instance.Window.Handle` | SDL 窗口指针（非 HWND！转 HWND 需 SDL_GetWindowWMInfo） |

### Everest.Events.Engine 区域（Everest.cs ~L57 起）
`Events` 是嵌套静态类，分区域：`CustomBirdTutorial / Everest / AssetReload / MapMeta /
SubHudRenderer / AngryOshiro / Celeste / Decal / EventTrigger / GameLoader / Input /
Level / LevelEnter / LevelLoader / Atlas / FileSelectSlot / OuiJournal / Journal /
OuiMainMenu / MainMenu ...`
（注意：**没有** `Events.Engine.OnUpdate` 之类；每帧 hook 用 `On.Monocle.Engine.Update`）

## 四、`Celeste.Mod.TextInput` 行为（本 mod 的研究核心）

```csharp
public static class TextInput {
    public static bool Initialized { get; private set; }
    private static event Action<char> _OnInput;      // ← 本 mod 反射读取此字段
    public static event Action<char> OnInput {
        add    { _OnInput += value; CheckTextStatus(); }
        remove { _OnInput -= value; CheckTextStatus(); }
    }
    internal static void Initialize(Game game) { /* 订阅 TextInputEXT.TextInput; CheckTextStatus */ }
    internal static void Shutdown() { if (IsTextInputActive()) StopTextInput(); ... }
    private static void CheckTextStatus() {
        if (_OnInput 从无到有 && !active)  StartTextInput();
        else if (_OnInput 归零 &&  active) StopTextInput();
    }
}
```

- `Initialize()` 由 `Everest.Initialize()` 调用（Everest.cs ~L8530）；`Shutdown()` 在退出时。
- **mod 的 `Load()` 早于 `TextInput.Initialize` 完成**（实测 `Load` 时 `Initialized=False`，
  首帧 Update 时才 True）→ 本 mod 用 `everestTextInputReady` 延迟守卫。

## 五、MiaoNet 聊天输入生命周期（参考实现）

`ChatComponent`（继承 `MiaoNetComponent`）：
- 按聊天键 → `Activate()`：`inputBox.Activate()`（订阅 `OnInput` + `TextEditing`），
  `Engine.Scene.Paused = true`
- Esc / Enter / 断线 → `Deactivate()`：`inputBox.Deactivate()`（退订，清 buffer），恢复 Paused
- `InputBox.Render()` 里 `TextInputEXT.SetInputRectangle(...)` 让 IME 候选框跟随输入框

## 六、给未来改动的提醒

1. **判断"当前是否有文本框想用 IME"** = 反射读 `TextInput._OnInput` 的调用列表长度
   （Everest 未公开此查询；若未来 Everest 加了公开 API 应替换）。
2. **hook 每帧**用 `On.Monocle.Engine.Update`；避免用裸 `new Hook(...)`（签名易错）。
3. 引用程序集只需 `Celeste.dll` + `FNA.dll` + `MMHOOK_Celeste.dll`（+ MonoMod 若用 detour）。
4. `nameof`/`typeof` 里写 `Celeste.Mod.TextInput` 时因命名空间嵌套易解析错位，用 `global::` 前缀。

## 七、相关 reference 文件

- `00-Everest-TextInput.cs`、`05-CelesteMod-TextInput-runtime.cs`：TextInput 两版反编译
- `01-MiaoNet-InputBox.cs`、`02-MiaoNet-ChatComponent.cs`：MiaoNet 聊天输入
- `04-MMHOOK-On.Monocle.Engine.cs`：On.* 签名
- `06-FNA-FNAPlatform.cs`：TextInputEXT → SDL 转发确认
