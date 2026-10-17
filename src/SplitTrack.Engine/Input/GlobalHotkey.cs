using System.ComponentModel;
using System.Runtime.InteropServices;
using SplitTrack.Common.Diagnostics;
using SplitTrack.Common.Time;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SplitTrack.Engine.Input;

/// <summary>Another program already owns the key combination.</summary>
public sealed class HotkeyUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// A system-wide hotkey (works while a game has focus) via RegisterHotKey, on its own message-loop
/// thread. The callback runs on that thread with the press instant; keep it short.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 1;

    private readonly HotkeyGesture _gesture;
    private readonly Action<long> _pressed;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _registered = new();
    private Exception? _registerError;
    private uint _threadId;

    private GlobalHotkey(HotkeyGesture gesture, Action<long> pressed)
    {
        _gesture = gesture;
        _pressed = pressed;
        _thread = new Thread(Run) { IsBackground = true, Name = $"SplitTrack hotkey {gesture}" };
    }

    public HotkeyGesture Gesture => _gesture;

    /// <param name="pressed">Receives the QPC instant of each press (auto-repeat is ignored).</param>
    /// <exception cref="HotkeyUnavailableException">Another program has registered the same combination.</exception>
    public static GlobalHotkey Register(HotkeyGesture gesture, Action<long> pressed)
    {
        var hotkey = new GlobalHotkey(gesture, pressed);
        hotkey._thread.Start();
        hotkey._registered.Wait();
        if (hotkey._registerError is { } error)
        {
            hotkey._thread.Join();
            throw new HotkeyUnavailableException($"{gesture} is already used by another program; choose another hotkey.", error);
        }

        Log.Info($"Hotkey {gesture} registered");
        return hotkey;
    }

    public void Dispose()
    {
        if (_thread.IsAlive)
        {
            PInvoke.PostThreadMessage(_threadId, PInvoke.WM_QUIT, default, default);
            _thread.Join();
        }

        _registered.Dispose();
    }

    private void Run()
    {
        // The message queue must exist before another thread can post WM_QUIT to it.
        PInvoke.PeekMessage(out _, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
        _threadId = PInvoke.GetCurrentThreadId();

        HOT_KEY_MODIFIERS modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        modifiers |= _gesture.Ctrl ? HOT_KEY_MODIFIERS.MOD_CONTROL : 0;
        modifiers |= _gesture.Alt ? HOT_KEY_MODIFIERS.MOD_ALT : 0;
        modifiers |= _gesture.Shift ? HOT_KEY_MODIFIERS.MOD_SHIFT : 0;
        modifiers |= _gesture.Win ? HOT_KEY_MODIFIERS.MOD_WIN : 0;
        if (!PInvoke.RegisterHotKey(HWND.Null, HotkeyId, modifiers, _gesture.VirtualKey))
        {
            _registerError = new Win32Exception(Marshal.GetLastWin32Error());
            _registered.Set();
            return;
        }

        _registered.Set();
        try
        {
            // GetMessage returns 0 on WM_QUIT and -1 on failure.
            while (PInvoke.GetMessage(out MSG message, HWND.Null, 0, 0).Value > 0)
            {
                if (message.message != PInvoke.WM_HOTKEY || message.wParam.Value != HotkeyId)
                {
                    continue;
                }

                long now = QpcClock.Now;
                try
                {
                    _pressed(now);
                }
                catch (Exception ex)
                {
                    Log.Error($"Hotkey {_gesture} handler failed", ex);
                }
            }
        }
        finally
        {
            PInvoke.UnregisterHotKey(HWND.Null, HotkeyId);
        }
    }
}
