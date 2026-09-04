using System.Globalization;
using System.Windows;

namespace HeadTracker.App.Services;

/// <summary>
/// Runtime UI language switching. Each language is a ResourceDictionary under
/// Languages/Strings.{lang}.xaml; Apply swaps the merged dictionary on
/// Application.Resources, and every XAML string uses DynamicResource so open
/// windows update immediately.
/// </summary>
public static class LanguageService
{
    /// <summary>Raised on the UI thread after a language dictionary has been swapped.</summary>
    public static event Action? LanguageChanged;

    public static string Current { get; private set; } = "en";

    /// <summary>Resolve the config value ("auto"/"en"/"zh") into a concrete language code.</summary>
    public static string Resolve(string? setting) => setting switch
    {
        "en" => "en",
        "zh" => "zh",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en",
    };

    /// <summary>Swap the application-wide string dictionary. Must run on the UI thread.</summary>
    public static void Apply(string lang)
    {
        if (lang is not ("en" or "zh"))
        {
            lang = "en";
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.OriginalString ?? "";
            if (src.Contains("Strings.", StringComparison.OrdinalIgnoreCase))
            {
                merged.RemoveAt(i);
            }
        }

        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"Languages/Strings.{lang}.xaml", UriKind.Relative),
        });
        Current = lang;
        LanguageChanged?.Invoke();
    }
}

/// <summary>Lookup helper for strings built in code (status texts, hotkey messages).</summary>
public static class Loc
{
    public static string Tr(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
