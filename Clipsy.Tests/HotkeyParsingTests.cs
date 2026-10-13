using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class HotkeyParsingTests
{
    [Theory]
    [InlineData("PrintScreen", 0x2C)]
    [InlineData("Snapshot", 0x2C)]
    [InlineData("A", 0x41)]
    [InlineData("7", 0x37)]
    [InlineData("F12", 0x7B)]
    [InlineData("F13", 0x7C)]
    [InlineData("NumberPad5", 0x65)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Back", 0x08)]
    [InlineData("Multiply", 0x6A)]
    [InlineData("186", 0xBA)]
    [InlineData("Escape", 0x1B)]
    public void KnownKeysParse(string name, int vk)
        => Assert.Equal((uint)vk, HotkeyService.KeyNameToVk(name));

    [Theory]
    [InlineData("")]
    [InlineData("Shift")]
    [InlineData("Control")]
    [InlineData("LeftWindows")]
    [InlineData("NotAKey")]
    [InlineData("999")]
    public void InvalidKeysAreRejected(string name)
        => Assert.Equal(0u, HotkeyService.KeyNameToVk(name));

    [Theory]
    [InlineData("Ctrl+Shift+S", true)]
    [InlineData("Alt+NumberPad0", true)]
    [InlineData("A", true)]
    [InlineData("Win+Alt+NumberPad0", false)]
    [InlineData("Ctrl+", false)]
    [InlineData("Ctrl+Shift", false)]
    [InlineData("Hyper+A", false)]
    public void BindingValidation(string binding, bool valid)
        => Assert.Equal(valid, HotkeyService.IsValidBinding(binding));

    [Theory]
    [InlineData("Escape")]
    [InlineData("Esc")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Win+S")]
    [InlineData("Windows+Shift+S")]
    [InlineData("Win+PrintScreen")]
    public void EscAndWinAreNeverAllowed(string binding)
    {
        Assert.Equal(HotkeyProblem.Reserved, HotkeyService.CheckBinding(binding, global: false));
        Assert.Equal(HotkeyProblem.Reserved, HotkeyService.CheckBinding(binding, global: true));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData("Space")]
    [InlineData("Enter")]
    [InlineData("Tab")]
    [InlineData("Back")]
    [InlineData("Left")]
    [InlineData("Delete")]
    [InlineData("Shift+A")]
    public void GlobalTypingKeysNeedCtrlOrAlt(string binding)
    {
        Assert.Equal(HotkeyProblem.NeedsModifier, HotkeyService.CheckBinding(binding, global: true));
        Assert.True(HotkeyService.IsValidBinding("Ctrl+" + binding.Replace("Shift+", ""), global: true));
    }

    [Theory]
    [InlineData("PrintScreen")]
    [InlineData("Snapshot")]
    [InlineData("Pause")]
    [InlineData("Scroll")]
    [InlineData("F1")]
    [InlineData("F24")]
    [InlineData("Shift+F5")]
    [InlineData("179")] // media play/pause
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+M")]
    public void GlobalBindingsThatCantBreakTyping(string binding)
        => Assert.True(HotkeyService.IsValidBinding(binding, global: true));

    [Theory]
    [InlineData("LeftWindows", true)]
    [InlineData("Escape", true)]
    [InlineData("91", true)]
    [InlineData("Alt", false)]
    [InlineData("F6", false)]
    public void ReservedKeyNames(string name, bool reserved)
        => Assert.Equal(reserved, HotkeyService.IsReservedKey(name));

    [Theory]
    [InlineData("PrintScreen", "Snapshot", true)]
    [InlineData("Ctrl+Shift+S", "shift+control+s", true)]
    [InlineData("Ctrl+S", "Ctrl+Shift+S", false)]
    [InlineData("", "", false)]
    public void SameBindingIgnoresSpelling(string a, string b, bool same)
        => Assert.Equal(same, HotkeyService.SameBinding(a, b));

    [Fact]
    public void DefaultBindingsAreValid()
    {
        var d = new AppSettings();
        Assert.True(HotkeyService.IsValidBinding(d.HotkeyCapture, global: true));
        foreach (var b in new[] { d.HotkeyScreenshotSilent, d.HotkeyCopy, d.HotkeyUndo, d.HotkeyRedo, d.HotkeySelectAll, d.HotkeySelectMonitor })
            Assert.True(HotkeyService.IsValidBinding(b));
    }
}
