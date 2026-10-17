using SplitTrack.Common.Diagnostics;
using SplitTrack.Engine.Settings;

namespace SplitTrack.RecordCli.Commands;

internal static class SettingsCommand
{
    /// <summary>'settings [--settings path]': prints the file's settings, or the defaults when there is none yet.</summary>
    public static int Show(string[] args)
    {
        if (args.Length != 0 && !(args.Length == 2 && args[0].Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            throw new UsageException("'settings' only takes --settings <path>; to change settings use 'settings save <options>'.");
        }

        string path = Path.GetFullPath(RecordOptions.FindSettingsPath(args) ?? SettingsFile.DefaultPath);
        RecorderSettings settings = SettingsFile.Load(path);

        Console.WriteLine(File.Exists(path)
            ? $"Settings file: {path}"
            : $"Settings file: {path} (does not exist yet; showing the defaults, 'settings save' creates it)");
        Console.WriteLine(SettingsFile.Serialize(settings));
        Console.WriteLine($"Library folder: {SafeLibraryRoot(settings)}");

        IReadOnlyList<string> errors = settings.Validate();
        if (errors.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(RecordOptions.DescribeErrors(path, errors, withCommandLine: false));
            return 1;
        }

        return 0;
    }

    /// <summary>'settings save [options]': applies record/replay options to the file and writes it.</summary>
    public static int Save(RecordOptions options)
    {
        bool existed = options.SettingsFileExists;
        SettingsFile.Save(options.SettingsPath, options.Settings);
        Log.Info($"Settings saved to {options.SettingsPath}");

        Console.WriteLine($"{(existed ? "Updated" : "Created")} {options.SettingsPath}:");
        Console.WriteLine(SettingsFile.Serialize(options.Settings));
        return 0;
    }

    private static string SafeLibraryRoot(RecorderSettings settings)
    {
        try
        {
            return settings.LibraryRoot;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"(unusable: {settings.LibraryFolder})";
        }
    }
}
