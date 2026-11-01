namespace FreeTapes;

// One transition per press/release, with physical K kept separate from injected K.
public sealed class ProximityHold(Func<bool, bool> send)
{
    public bool PhysicalKDown { get; private set; }
    public bool Holding { get; private set; }
    public bool SetPhysicalK(bool down)
    {
        PhysicalKDown = down;
        // The physical key-up also releases the game's key state.
        if (!down) Holding = false;
        return down;
    }
    public bool Update(bool enabled, bool gameFocused, bool routed, bool capturing, bool musicPlaying, bool talking)
    {
        var wanted = enabled && gameFocused && routed && !capturing && (musicPlaying || talking);
        if (!wanted) return Release();
        if (Holding || PhysicalKDown) return true;
        if (!send(true)) return false;
        Holding = true;
        return true;
    }
    public bool Release()
    {
        if (!Holding) return true;
        if (!PhysicalKDown && !send(false)) return false;
        Holding = false;
        return true;
    }
}
