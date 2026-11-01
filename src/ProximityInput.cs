using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeTapes;
public sealed class ProximityInput : IDisposable
{
    private const int K = 0x4B;
    private readonly object gate = new();
    private readonly Hook callback;
    private readonly ProximityHold hold;
    private nint hook;
    private nint lastWindow;
    private bool gameFocused;
    public bool Holding { get { lock (gate) return hold.Holding; } }
    public bool GameHasFocus { get { RefreshFocus(); return gameFocused; } }
    public ProximityInput()
    {
        hold = new ProximityHold(SendK);
        hold.SetPhysicalK((GetAsyncKeyState(K) & 0x8000) != 0);
        callback = ObserveKey;
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe the physical K key. Automatic proximity is unavailable.");
    }
    public bool IsTalkInputDown(int virtualKey)
    {
        lock (gate) return virtualKey == K ? hold.PhysicalKDown : (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
    public bool Update(bool enabled, bool routed, bool capturing, bool musicPlaying, bool talking)
    {
        RefreshFocus();
        lock (gate) return hold.Update(enabled, gameFocused, routed, capturing, musicPlaying, talking);
    }
    private void RefreshFocus()
    {
        var window = GetForegroundWindow();
        // Resolve only when focus changes, not on every audio/UI tick.
        if (window != lastWindow)
        {
            lastWindow = window; gameFocused = false;
            if (window != 0)
            {
                GetWindowThreadProcessId(window, out var pid);
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
                    gameFocused = process.ProcessName.StartsWith("Wardogs", StringComparison.OrdinalIgnoreCase)
                        || title.ToString().Equals("WARDOGS", StringComparison.OrdinalIgnoreCase)
                        || title.ToString().StartsWith("WARDOGS ", StringComparison.OrdinalIgnoreCase);
                }
                catch (ArgumentException) { }
                catch (Win32Exception) { }
            }
        }
    }
    public void Release() { lock (gate) hold.Release(); }
    private nint ObserveKey(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var input = Marshal.PtrToStructure<HookData>(data);
            if (input.Key == K && (input.Flags & 0x10) == 0)
                lock (gate) hold.SetPhysicalK((input.Flags & 0x80) == 0);
        }
        return CallNextHookEx(hook, code, message, data);
    }
    private static bool SendK(bool down)
    {
        var input = new Input { Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Scan = 0x25, Flags = 0x08u | (down ? 0 : 0x02u) } } };
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }
    public void Dispose()
    {
        Release();
        if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; }
    }
    private delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct HookData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
}
