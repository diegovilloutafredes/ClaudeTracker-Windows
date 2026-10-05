using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ClaudeTracker.Core;

/// <summary>
/// Localized strings, keyed by the English text exactly as the macOS app keys them.
///
/// The macOS string catalog (<c>ClaudeTracker/Localizable.xcstrings</c>) is embedded as-is, so a
/// string both apps share is translated once. Strings only Windows shows live in
/// <c>Windows.strings.json</c>. A key with no translation renders as its English text.
/// </summary>
public static class L
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Translations = new(Load);
    private static readonly ConcurrentDictionary<string, string> CompositeFormats = new();

    /// <summary>Two-letter language to use instead of the UI culture's. Tests pin this.</summary>
    public static string? LanguageOverride { get; set; }

    private static string Language => LanguageOverride ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>The translation of <paramref name="key"/>, verbatim.</summary>
    public static string T(string key) => Lookup(Language, key);

    /// <summary>
    /// The translation of a format key, filled in. Keys keep the catalog's printf specifiers
    /// (<c>%@</c>, <c>%d</c>, <c>%lld</c>, <c>%%</c>), so they match the Swift source one to one.
    /// </summary>
    public static string F(string key, params object?[] args) => Format(Language, key, args);

    /// <summary>The translation of <paramref name="key"/> in a given language — no shared state involved.</summary>
    internal static string Lookup(string language, string key) =>
        Translations.Value.TryGetValue(language, out var table) && table.TryGetValue(key, out var value) ? value : key;

    internal static string Format(string language, string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, CompositeFormats.GetOrAdd(Lookup(language, key), ToCompositeFormat), args);

    /// <summary>Converts a printf-style pattern to a .NET composite format string.</summary>
    internal static string ToCompositeFormat(string pattern)
    {
        var sb = new StringBuilder(pattern.Length + 8);
        var next = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c is '{' or '}') { sb.Append(c).Append(c); continue; }
            if (c != '%' || i + 1 >= pattern.Length) { sb.Append(c); continue; }
            if (pattern[i + 1] == '%') { sb.Append('%'); i++; continue; }

            // %[n$](@|d|ld|lld)
            var j = i + 1;
            var digits = j;
            while (digits < pattern.Length && char.IsAsciiDigit(pattern[digits])) digits++;
            int? position = null;
            if (digits > j && digits < pattern.Length && pattern[digits] == '$')
            {
                position = int.Parse(pattern.AsSpan(j, digits - j), CultureInfo.InvariantCulture) - 1;
                j = digits + 1;
            }
            var end = j;
            while (end < pattern.Length && pattern[end] == 'l') end++;
            if (end < pattern.Length && pattern[end] is '@' or 'd')
            {
                sb.Append('{').Append(position ?? next).Append('}');
                if (position is null) next++;
                i = end;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        void Add(string language, string key, string value)
        {
            if (!result.TryGetValue(language, out var table)) result[language] = table = new(StringComparer.Ordinal);
            table[key] = value;
        }

        var assembly = typeof(L).Assembly;
        // Apple String Catalog: strings.<key>.localizations.<language>.stringUnit.value
        using (var catalog = Parse(assembly, "ClaudeTracker.Core.Localizable.xcstrings"))
        {
            if (catalog.RootElement.TryGetProperty("strings", out var strings) && strings.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in strings.EnumerateObject())
                {
                    if (!entry.Value.TryGetProperty("localizations", out var localizations)
                        || localizations.ValueKind != JsonValueKind.Object) continue;
                    foreach (var localization in localizations.EnumerateObject())
                    {
                        if (localization.Value.TryGetProperty("stringUnit", out var unit)
                            && unit.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            Add(localization.Name, entry.Name, value.GetString()!);
                        }
                    }
                }
            }
        }
        // Windows-only strings: { "<key>": { "<language>": "<value>" } }
        using (var windows = Parse(assembly, "ClaudeTracker.Core.Windows.strings.json"))
        {
            foreach (var entry in windows.RootElement.EnumerateObject())
            {
                foreach (var localization in entry.Value.EnumerateObject())
                {
                    Add(localization.Name, entry.Name, localization.Value.GetString()!);
                }
            }
        }
        return result;
    }

    private static JsonDocument Parse(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {resourceName}");
        return JsonDocument.Parse(stream);
    }
}
