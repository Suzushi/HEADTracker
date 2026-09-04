using YamlDotNet.Serialization;

namespace HeadTracker.Core.Configuration;

/// <summary>
/// YAML persistence for <see cref="TrackerSettings"/>.
/// Missing keys keep their defaults, unknown keys are ignored, so configs
/// written by either the legacy C++ app or this one load cleanly.
/// </summary>
public static class SettingsStore
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .Build();

    /// <summary>Set when the last <see cref="Load"/> fell back to defaults because the
    /// file was corrupt/unreadable; null on a clean load. The UI can surface this.</summary>
    public static string? LastLoadError { get; private set; }

    public static TrackerSettings Load(string path)
    {
        LastLoadError = null;
        TrackerSettings settings;
        try
        {
            if (File.Exists(path))
            {
                var yaml = File.ReadAllText(path);
                settings = Deserializer.Deserialize<TrackerSettings?>(yaml) ?? new TrackerSettings();
            }
            else
            {
                settings = new TrackerSettings();
            }
        }
        catch (Exception ex)
        {
            // A malformed config.yaml (e.g. a hand-edit typo like two keys on one line)
            // must never brick the app. Quarantine the bad file, log the reason, and
            // start from defaults so the user can recover via the settings UI.
            LastLoadError = ex.Message;
            Quarantine(path, ex);
            settings = new TrackerSettings();
        }

        settings.Normalize();
        return settings;
    }

    private static void Quarantine(string path, Exception ex)
    {
        try
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            string backup = Path.Combine(dir, "config.bad.yaml");
            if (File.Exists(path))
            {
                File.Copy(path, backup, overwrite: true);
            }
            File.AppendAllText(Path.Combine(dir, "config-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to parse '{path}': {ex.Message}{Environment.NewLine}" +
                $"Offending file copied to '{backup}'; loaded defaults instead.{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Quarantine/logging must never become the next failure.
        }
    }

    public static void Save(string path, TrackerSettings settings)
    {
        var yaml = Serializer.Serialize(settings);
        File.WriteAllText(path, yaml);
    }
}
