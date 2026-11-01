namespace FreeTapes;

public enum PlaybackAction { Play, Pause, Stop, Next }

public sealed class PlaybackBindings
{
    private readonly Dictionary<PlaybackAction, int> keys = Enum.GetValues<PlaybackAction>().ToDictionary(a => a, _ => 0);
    private readonly Dictionary<PlaybackAction, bool> wasDown = Enum.GetValues<PlaybackAction>().ToDictionary(a => a, _ => true);
    public int this[PlaybackAction action] => keys[action];
    public bool Conflicts(int key) => key != 0 && keys.Values.Contains(key);
    public bool Set(PlaybackAction action, int key)
    {
        if (key is < 0 or > 254 || (key != 0 && keys.Any(p => p.Key != action && p.Value == key))) return false;
        keys[action] = key;
        // Capturing/loading an input must not execute its action until released.
        wasDown[action] = true;
        return true;
    }
    public IReadOnlyList<PlaybackAction> Poll(Func<int, bool> isDown, bool enabled)
    {
        var pressed = new List<PlaybackAction>();
        foreach (var action in keys.Keys)
        {
            var down = keys[action] != 0 && isDown(keys[action]);
            if (enabled && down && !wasDown[action]) pressed.Add(action);
            wasDown[action] = down;
        }
        return pressed;
    }
    public static int NextIndex(int count, int currentIndex, int selectedIndex)
    {
        if (count <= 0) return -1;
        var index = currentIndex >= 0 && currentIndex < count ? currentIndex : selectedIndex;
        return index >= 0 && index < count ? (index + 1) % count : 0;
    }
}
