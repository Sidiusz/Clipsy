using Clipsy.Localization;
using Microsoft.UI.Xaml.Controls;

namespace Clipsy.Controls;

public sealed partial class ExperimentalBadge : UserControl
{
    public ExperimentalBadge()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshText();
        WarningTip.Opened += (_, _) => RefreshText();
    }

    private void RefreshText()
        => WarningText.Text = Strings.Get("ExperimentalWarning");
}