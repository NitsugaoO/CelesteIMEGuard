using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.IMEGuard;

/// <summary>
/// CelesteIMEGuard — an "IMBlocker"-style IME guard for Celeste (Everest).
///
/// Problem
/// -------
/// On Windows, the OS IME (e.g. Microsoft Pinyin) is global. At game launch — before any
/// chat box has ever been opened — neither Everest nor FNA/SDL has ever called
/// SDL_StartTextInput / SDL_StopTextInput. On SDL 2.28 (the build Celeste ships),
/// the first call to either of those triggers SDL's internal IMM_Init, whose very first
/// action is IMM_Disable → ImmAssociateContext(hwnd, NULL), i.e. it DETACHES the Win32
/// IME context from the window. Because that first call never happens at startup, the
/// system IME stays attached to the game window, and pressing Shift / typing keys can pop
/// the IME candidate window over the game.
///
/// Fix (same idea as the Minecraft mod IMBlocker)
/// ----------------------------------------------
///   * Shortly after the game window exists, call StopTextInput() once. This makes SDL
///     initialize its IME state and immediately detach the IME context — no candidate
///     popup can appear while playing.
///   * Every frame, if SDL text input is active but no text box is subscribed to
///     Everest's TextInput.OnInput (i.e. we're playing, not typing), call StopTextInput()
///     again — a cheap safety net for anything that re-enables input unexpectedly.
///   * When a text box IS subscribed (chat open), we do nothing: Everest's StartTextInput
///     makes SDL re-attach the IME context, so Chinese composition keeps working.
///
/// This mod never touches the IME while a text box is open, and uses SDL's own IME
/// management rather than raw imm32 P/Invoke, so it stays in sync with the game.
/// </summary>
public sealed class IMEGuardModule : EverestModule
{
    public static IMEGuardModule Instance { get; private set; } = null!;

    // Everest keeps the event backing field private; we read its invocation list to know
    // whether ANY gameplay text box is currently subscribed (i.e. wants the IME on).
    private static readonly FieldInfo onInputField =
        typeof(global::Celeste.Mod.TextInput).GetField("_OnInput", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(nameof(global::Celeste.Mod.TextInput), "_OnInput");

    private bool subscribed;
    private bool everestTextInputReady;
    private bool startupStopDone;      // did our one-shot startup StopTextInput yet
    private int framesUntilStartupStop = 10; // wait a few frames for the window to exist
    private bool loggedDetach;
    private int lastLoggedSubscribers = -1;

    public IMEGuardModule()
    {
        Instance = this;
    }

    public override void Load()
    {
        Everest.Events.Celeste.OnExiting += Unload;

        Logger.Info("IMEGuard", "CelesteIMEGuard v1.2.0 loaded. TextInput.Initialized=" + global::Celeste.Mod.TextInput.Initialized);

        // Per-frame safety net. On.Monocle.Engine.Update fires every game loop iteration.
        On.Monocle.Engine.Update += EngineUpdate;
        // When the game window regains focus, re-assert the IME state (covers alt-tab etc.).
        On.Monocle.Engine.OnActivated += EngineOnActivated;
        subscribed = true;
    }

    public override void Unload()
    {
        if (!subscribed)
            return;
        subscribed = false;
        On.Monocle.Engine.Update -= EngineUpdate;
        On.Monocle.Engine.OnActivated -= EngineOnActivated;
        Everest.Events.Celeste.OnExiting -= Unload;
    }

    private void EngineOnActivated(On.Monocle.Engine.orig_OnActivated orig, Monocle.Engine self, object sender, EventArgs args)
    {
        orig(self, sender, args);
        CheckAndGuardIme();
    }

    private void EngineUpdate(On.Monocle.Engine.orig_Update orig, Monocle.Engine self, GameTime gameTime)
    {
        orig(self, gameTime);
        CheckAndGuardIme();
    }

    private void CheckAndGuardIme()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // SDL's IME detach logic is what we rely on; other OSes differ.

        // Only guard once Everest's TextInput is initialized.
        if (!everestTextInputReady)
        {
            everestTextInputReady = global::Celeste.Mod.TextInput.Initialized;
            return;
        }

        // One-shot startup nudge: call StopTextInput once the window is up. On SDL 2.28
        // this triggers IMM_Init → IMM_Disable, which detaches the IME context from the
        // window. This is the core fix for "IME still pops up right after launch".
        if (!startupStopDone)
        {
            if (--framesUntilStartupStop > 0)
                return;
            startupStopDone = true;
            try
            {
                TextInputEXT.StopTextInput();
                Logger.Info("IMEGuard", "Startup StopTextInput issued (SDL IME detached from window). IsTextInputActive=" + SafeIsTextInputActive());
            }
            catch (Exception e)
            {
                Logger.Warn("IMEGuard", "Startup StopTextInput failed: " + e.Message);
            }
            return;
        }

        // Steady-state safety net: if SDL text input is active but no text box wants it,
        // turn it off. This handles anything that re-enabled input while playing.
        bool textInputActive = SafeIsTextInputActive();

        // Log transitions so we can verify chat-open/close restores & detaches the IME.
        int subs = GetSubscriberCount();
        if (subs != lastLoggedSubscribers)
        {
            lastLoggedSubscribers = subs;
            Logger.Info("IMEGuard", "TextInput.OnInput subscribers=" + subs
                + " IsTextInputActive=" + textInputActive);
        }

        if (!textInputActive)
            return;

        if (HasTextInputSubscriber())
            return; // a chat/text box is open — leave the IME to its owner (Everest).

        if (!loggedDetach)
        {
            loggedDetach = true;
            Logger.Info("IMEGuard", "Safety net: stopping text input (no active text box).");
        }
        try
        {
            TextInputEXT.StopTextInput();
        }
        catch
        {
            // best effort
        }
    }

    private static bool SafeIsTextInputActive()
    {
        try
        {
            return TextInputEXT.IsTextInputActive();
        }
        catch
        {
            return false;
        }
    }

    private static int GetSubscriberCount()
    {
        try
        {
            var handler = (Action<char>?)onInputField.GetValue(null);
            return handler?.GetInvocationList().Length ?? 0;
        }
        catch
        {
            return -1;
        }
    }

    private static bool HasTextInputSubscriber()
    {
        int n = GetSubscriberCount();
        if (n < 0)
        {
            // Reflection failed (Everest internals changed) — conservatively assume a
            // subscriber exists so we never yank the IME out from under a live text box.
            return true;
        }
        return n > 0;
    }
}
