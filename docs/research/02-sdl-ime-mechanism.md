# SDL 2.28.5 Windows IME 机制详解

> 归档日期：2026-09-07 ｜ 依据：SDL 2.28.5 官方源码 `src/video/windows/SDL_windowskeyboard.c`（已存 reference/03）

## 一、整体流程

SDL2 的 IME 是跨平台 + Windows 驱动的两层结构：

```
SDL_StartTextInput()  [src/events/SDL_keyboard.c]
  └→ 标记 window 的 textinput flag
  └→ 调 video driver 的 StartTextInput
       └→ WIN_StartTextInput [src/video/windows/SDL_windowskeyboard.c]
            ├→ IME_Init(videodata, hwnd)   首次初始化（之后空转）
            └→ IME_Enable(videodata, hwnd) 关联 IMC → 接收 WM_IME_* 消息
```

## 二、关键函数（行号以 2.28.5 为准）

| 函数 | 行号 | 行为 |
|---|---|---|
| `WIN_StartTextInput` | ~198 | `IME_Init` + `IME_Enable` |
| `WIN_StopTextInput` | ~218 | `IME_Init` + `IME_Disable` ← **无"必须先 Start"守卫** |
| `IME_Init` | ~375 | 首次初始化；`ime_himc = ImmGetContext(hwnd)`；末尾**立即 `IME_Disable`** |
| `IME_SetWindow` | ~721 | `ime_hwnd_current = hwnd`（并 AssociateFocus） |
| `IME_Enable` | ~413 | 若 `ime_hwnd_current == ime_hwnd_main`：`ImmAssociateContext(hwnd, ime_himc)` |
| `IME_Disable` | ~430 | `IME_ClearComposition`；若 `current==main`：`ImmAssociateContext(hwnd, (HIMC)0)` |
| `IME_Quit` | ~445 | 退出时 `ImmAssociateContext(hwnd, ime_himc)` 还原 |

## 三、核心代码摘录

```c
void WIN_StopTextInput(_THIS) {
    WIN_ResetDeadKeys();
    window = SDL_GetKeyboardFocus();
    if (window) {
        HWND hwnd = ((SDL_WindowData *)window->driverdata)->hwnd;
        IME_Init(videodata, hwnd);
        IME_Disable(videodata, hwnd);   // ← 摘除 IME
    }
}

static void IME_Init(SDL_VideoData *videodata, HWND hwnd) {
    if (videodata->ime_initialized) return;   // 只初始化一次
    videodata->ime_hwnd_main = hwnd;
    // ... CoCreateInstance TSF ThreadMgr, 加载 imm32.dll ...
    IME_SetWindow(videodata, hwnd);           // ime_hwnd_current = hwnd
    videodata->ime_himc = ImmGetContext(hwnd); // 存默认 IMC
    ImmReleaseContext(hwnd, videodata->ime_himc);
    if (!videodata->ime_himc) { ime_available = FALSE; IME_Disable(...); return; }
    // ...
    IME_Disable(videodata, hwnd);             // ← 初始化完立即摘除！
}

static void IME_Enable(SDL_VideoData *videodata, HWND hwnd) {
    if (!videodata->ime_initialized || !videodata->ime_hwnd_current) return;
    if (!videodata->ime_available) { IME_Disable(videodata, hwnd); return; }
    if (videodata->ime_hwnd_current == videodata->ime_hwnd_main)
        ImmAssociateContext(videodata->ime_hwnd_current, videodata->ime_himc);  // 关联回
    videodata->ime_enabled = SDL_TRUE;
    // ...
}

static void IME_Disable(SDL_VideoData *videodata, HWND hwnd) {
    if (!videodata->ime_initialized || !videodata->ime_hwnd_current) return;
    IME_ClearComposition(videodata);
    if (videodata->ime_hwnd_current == videodata->ime_hwnd_main)
        ImmAssociateContext(videodata->ime_hwnd_current, (HIMC)0);   // ← 摘除 (NULL)
    videodata->ime_enabled = SDL_FALSE;
    // ...
}
```

## 四、对本项目最重要的推论

1. **`IME_Init` 里"初始化完立即 `IME_Disable`"** → 只要 SDL 的 Start 或 Stop 被调过一次，
   IME 就被摘除（除非之后 Enable 又关联回）。
2. **`IME_Init` 只在 `ime_initialized == FALSE` 时工作一次** → 摘除/关联是**有状态**的，
   反复调 Stop 不会反复摘除（第二次起 `ime_initialized` 已 true，`IME_Disable` 幂等）。
3. **`WIN_StopTextInput` 没有"必须先 Start"的守卫** → 启动后直接调 `StopTextInput` 是**安全且有效**的，
   它会触发首次 `IME_Init`（内含摘除）。
4. `IME_Disable` 有 `ime_hwnd_current == ime_hwnd_main` 条件；`IME_Init` 里 `IME_SetWindow`
   已把 current 设为传入的 hwnd（通常是主窗口），条件成立。

## 五、版本差异提醒

- **SDL 3** 把文本输入移到了 video driver 的 `StartTextInput` 方法里，结构不同。
- 较老 SDL2 的 `IME_Disable` 行为可能不同（本项目实测 2.28.5，Celeste 内置版本）。
- 升级 SDL/FNA 后需重测"启动调 StopTextInput 仍能摘除"。

## 六、相关

- 完整源码：`../reference/03-SDL2.28-windowskeyboard-IME.c`（git tag `release-2.28.5`）
- 根因分析：`01-root-cause.md`
