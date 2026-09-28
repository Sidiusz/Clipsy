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
    [InlineData("Win+Alt+NumberPad0", true)]
    [InlineData("Ctrl+", false)]
    [InlineData("Ctrl+Shift", false)]
    public void BindingValidation(string binding, bool valid)
        => Assert.Equal(valid, HotkeyService.IsValidBinding(binding));
}
