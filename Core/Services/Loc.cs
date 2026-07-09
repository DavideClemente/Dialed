using Microsoft.Windows.ApplicationModel.Resources;

namespace Dialed.Core.Services;

/// <summary>
/// Central string lookup against Strings/&lt;lang&gt;/Resources.resw. Uses an
/// explicit <see cref="ResourceContext"/> whose language we set from the app's
/// language setting, so lookups are deterministic and don't depend on ambient
/// culture or PrimaryLanguageOverride propagation.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager _manager = new();
    private static readonly ResourceMap _map = _manager.MainResourceMap.GetSubtree("Resources");
    private static ResourceContext _context = _manager.CreateResourceContext();

    /// <summary>Point lookups at a specific BCP-47 language, or empty to follow Windows.</summary>
    public static void SetLanguage(string? code)
    {
        var context = _manager.CreateResourceContext();
        if (!string.IsNullOrEmpty(code))
            context.QualifierValues["Language"] = code;
        _context = context;
    }

    public static string Get(string key)
    {
        try { return _map.GetValue(key, _context)?.ValueAsString ?? key; }
        catch { return key; }
    }

    public static string Get(string key, params object[] args) =>
        string.Format(Get(key), args);
}
