using System.Text.Json;

namespace DataMigration.Migration.Configuration;

public class ProfileLoader
{
    private readonly string _profilesDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ProfileLoader(string profilesDirectory)
    {
        _profilesDirectory = profilesDirectory;
    }

    public ProfileOptions Load(string profileName)
    {
        var profilePath = GetProfilePath(profileName);
        if (!File.Exists(profilePath))
            throw new FileNotFoundException($"Profile not found: {profileName} (looked in {profilePath})");

        var json = File.ReadAllText(profilePath);
        var profile = JsonSerializer.Deserialize<ProfileOptions>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize profile: {profileName}");

        return profile;
    }

    public IEnumerable<(string Name, string? Description)> ListProfilesWithDescriptions()
    {
        if (!Directory.Exists(_profilesDirectory))
            yield break;

        foreach (var file in Directory.GetFiles(_profilesDirectory, "*.json").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            string? description = null;
            try
            {
                var profile = Load(name!);
                description = profile.Description;
            }
            catch
            {
                description = "(error loading profile)";
            }

            yield return (name!, description);
        }
    }

    private string GetProfilePath(string profileName)
    {
        var fileName = profileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? profileName
            : $"{profileName}.json";

        return Path.Combine(_profilesDirectory, fileName);
    }
}
