namespace FreeTapes;

// Stores Windows virtual-key codes for both keyboard keys and mouse buttons.
public sealed class TalkBinding
{
    public int VirtualKey { get; private set; } = 0x56; // V
    public bool Capturing { get; private set; }
    private bool awaitingRelease;

    public void BeginCapture() { Capturing = true; awaitingRelease = true; }
    public void CancelCapture() => Capturing = false;
    public bool Set(int virtualKey)
    {
        if (virtualKey is < 1 or > 254) return false;
        VirtualKey = virtualKey;
        Capturing = false;
        awaitingRelease = true;
        return true;
    }
    public bool IsTalking(bool down)
    {
        if (Capturing) return false;
        if (awaitingRelease) { if (!down) awaitingRelease = false; return false; }
        return down;
    }
}
