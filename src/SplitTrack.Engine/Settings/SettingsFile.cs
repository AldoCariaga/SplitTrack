using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SplitTrack.Common.Platform;
using SplitTrack.Engine.Recording;

namespace SplitTrack.Engine.Settings;

/// <summary>Reads and writes <see cref="RecorderSettings"/> as JSON (comments and trailing commas allowed, unknown keys refused).</summary>
public static class SettingsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,

        // Hotkeys read "Alt+F10", not "Alt+F10"; the file is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,

        // A misspelt key would otherwise be ignored without a word.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>%LOCALAPPDATA%\SplitTrack\settings.json.</summary>
    public static string DefaultPath => AppPaths.Settings;

    /// <summary>The settings in <paramref name="path"/>, or the defaults when there is no such file. Not validated.</summary>
    /// <exception cref="SettingsException">The file cannot be read, is not valid settings JSON, or is from a newer version.</exception>
    public static RecorderSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new RecorderSettings();
        }

        RecorderSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SettingsException($"{path} is not valid: {Describe(ex)}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"{path} cannot be read: {ex.Message}", ex);
        }

        if (settings is null)
        {
            throw new SettingsException($"{path} is not valid: it holds null instead of the settings object.");
        }

        if (settings.Version > RecorderSettings.CurrentVersion)
        {
            throw new SettingsException(
                $"{path} is version {settings.Version}, written by a newer SplitTrack; this one reads up to version {RecorderSettings.CurrentVersion}.");
        }

        settings.FillMissing();
        settings.Version = RecorderSettings.CurrentVersion;
        return settings;
    }

    /// <summary>Writes through a temporary file, so a crash never leaves half a settings file.</summary>
    public static void Save(string path, RecorderSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(settings));
        File.Move(temp, path, overwrite: true);
    }

    public static string Serialize(RecorderSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>A deep copy, e.g. for a settings page to edit.</summary>
    public static RecorderSettings Clone(RecorderSettings settings) =>
        JsonSerializer.Deserialize<RecorderSettings>(Serialize(settings), JsonOptions)!;

    /// <summary>The serializer's message without .NET type names, e.g. "line 4, video.fps: not a valid value here".</summary>
    private static string Describe(JsonException ex)
    {
        string where = string.Join(", ", new[]
        {
            ex.LineNumber is { } line ? $"line {line + 1}" : null,
            ex.Path is { Length: > 1 } jsonPath ? jsonPath.TrimStart('$', '.') : null,
        }.OfType<string>());

        string message = ex.Message;
        int pathAt = message.IndexOf(" Path: ", StringComparison.Ordinal);
        string reason =
            message.StartsWith("The JSON property", StringComparison.Ordinal)
                ? $"unknown setting {message.Split('\'')[1]} (misspelt, or in the wrong section?)"
            : message.StartsWith("The JSON value could not be converted", StringComparison.Ordinal)
                ? ex.Path?.EndsWith(".kind", StringComparison.Ordinal) == true
                    ? "not a valid value here (use game, app, microphone or system)"
                    : "not a valid value here (check the type: number, text, true/false, list)"
            : pathAt >= 0 ? message[..pathAt] : message;
        return where.Length == 0 ? reason : $"{where}: {reason}";
    }

    /// <summary>The kind as written in the file: game, app, microphone, system.</summary>
    public static string KindName(AudioTrackKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());
}

/// <summary>The settings file is unreadable or the settings are unusable; the message says why.</summary>
public sealed class SettingsException(string message, Exception? inner = null) : Exception(message, inner);
