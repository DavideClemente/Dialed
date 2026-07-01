using Microsoft.UI.Xaml.Markup;

namespace AudioMixerWin.Core.Services;

/// <summary>
/// XAML markup extension for localized strings: <c>{loc:Loc Key=Some_Key}</c>.
/// Resolves once at load time (the app prompts for a restart on language change),
/// and works everywhere a string is expected — including attached properties like
/// ToolTipService.ToolTip and inside DataTemplates.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.Get(Key);
}
