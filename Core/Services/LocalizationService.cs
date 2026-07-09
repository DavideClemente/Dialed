using System.Collections.Generic;
using System.Globalization;
using Windows.Globalization;

namespace Dialed.Core.Services;

/// <summary>
/// A selectable UI language. <see cref="Code"/> is a BCP-47 tag, or empty for
/// "follow Windows".
/// </summary>
public sealed class LanguageOption
{
    public LanguageOption(string code, string displayName)
    {
        Code = code;
        DisplayName = displayName;
    }

    public string Code { get; }
    public string DisplayName { get; }
}

/// <summary>
/// Forces the app's language so the framework-provided UI (context menus,
/// NumberBox tooltips, dialogs) and .NET exception messages (e.g. the serial
/// COM-port error) all render in one chosen language instead of leaking the OS
/// language. Unpackaged apps do not persist <see cref="ApplicationLanguages
/// .PrimaryLanguageOverride"/>, so we store the choice in settings and re-apply
/// it on every launch.
/// </summary>
public static class LocalizationService
{
    // "System default" is itself localized, so build the list on demand rather than
    // caching it — Apply() runs before the first read, so Loc is already pointed at
    // the chosen language.
    public static IReadOnlyList<LanguageOption> Options => new[]
    {
        new LanguageOption("", Loc.Get("Lang_SystemDefault")),
        new LanguageOption("en-US", "English"),
        new LanguageOption("pt-PT", "Português (Portugal)"),
    };

    public static void Apply(string? code)
    {
        // Our own .resw lookups resolve against this explicit context.
        Loc.SetLanguage(code);

        // Framework-provided strings resolve against the primary language override.
        ApplicationLanguages.PrimaryLanguageOverride = string.IsNullOrEmpty(code)
            ? string.Empty
            : code;

        // Empty means "follow Windows" — leave the ambient culture untouched.
        if (string.IsNullOrEmpty(code))
            return;

        // Exception messages use CurrentUICulture; steer it (and future threads,
        // such as the serial reader) to the chosen language. Formatting culture is
        // left alone so number parsing/display is unaffected.
        var culture = new CultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
