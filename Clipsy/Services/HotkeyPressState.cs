namespace Clipsy.Services;

internal sealed class HotkeyPressState
{
    internal uint HeldKey { get; private set; }

    internal bool TryPress(uint key)
    {
        if (HeldKey == key) return false;
        HeldKey = key;
        return true;
    }

    internal void Release(uint key)
    {
        if (HeldKey == key) HeldKey = 0;
    }

    internal void ReleaseIfUp(bool physicallyDown)
    {
        if (!physicallyDown) HeldKey = 0;
    }
}
