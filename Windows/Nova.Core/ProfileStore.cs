using System.Text.Json;

namespace Nova.Core;

/// Loads and saves `profile.json` in %LOCALAPPDATA%\Nova (private to the Windows user).
public static class ProfileStore
{
    /// Tests point this at a temp folder.
    public static string? DirectoryOverride { get; set; }

    public static string DirectoryPath =>
        DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nova");

    public static string ProfilePath => Path.Combine(DirectoryPath, "profile.json");

    /// Set when the last `Load()` hit an unreadable profile.
    public static string? LoadError { get; private set; }

    public static Profile Load()
    {
        LoadError = null;
        string text;
        try { text = File.ReadAllText(ProfilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Profile.Starter(); }
        try
        {
            return Profile.FromJson(text);
        }
        catch (JsonException)
        {
            // Keep the unreadable file instead of silently overwriting it on the next save.
            var backup = Path.Combine(DirectoryPath, $"profile.broken-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json");
            try { File.Copy(ProfilePath, backup); }
            catch (IOException) { }
            LoadError = $"profile.json не читается, сохранён как {Path.GetFileName(backup)}. Начат новый профиль.";
            return Profile.Starter();
        }
    }

    public static void Save(Profile profile)
    {
        Directory.CreateDirectory(DirectoryPath);
        var tmp = ProfilePath + ".tmp";
        // Holds server keys: owner-only on Unix (test runs); %LOCALAPPDATA% is already private on Windows.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(tmp, options))
        using (var w = new StreamWriter(fs))
            w.Write(profile.ToJson());
        File.Move(tmp, ProfilePath, overwrite: true);
    }
}
