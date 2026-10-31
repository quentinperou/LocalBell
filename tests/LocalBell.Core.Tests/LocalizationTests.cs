using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LocalBell.Core.Tests;

/// <summary>Keeps the English and French UI strings (src/LocalBell.App/Properties/*.resx) in step with each other and with the code.</summary>
public class LocalizationTests
{
    private static readonly string AppDir = Path.Combine(FindRepositoryRoot(), "src", "LocalBell.App");

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LocalBell.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (LocalBell.slnx) not found.");
    }

    private static Dictionary<string, string> ReadStrings(string fileName) =>
        XDocument.Load(Path.Combine(AppDir, "Properties", fileName)).Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).ToHashSet();

    /// <summary>Keys referenced by the app: {local:Tr Key}, Strings.Get/Format("Key"), Strings.Count("Key") and the ("…", "Key") pairs of the menus.</summary>
    private static List<string> UsedKeys()
    {
        var keys = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string text = File.ReadAllText(file);
            keys.AddRange(Regex.Matches(text, @"\{local:Tr (\w+)\}").Select(m => m.Groups[1].Value));

            // First argument of Strings.Get/Format/Count, ternaries included: Strings.Get(a ? "KeyA" : "KeyB").
            foreach (Match call in Regex.Matches(text, @"Strings\.(Get|Format|Count)\(([^,)]*)"))
            {
                // A literal followed by "+" is a prefix ("DefaultMessage" + i), checked by DefaultTexts_Exist.
                var literals = Regex.Matches(call.Groups[2].Value, "\"(\\w+)\"(?!\\s*\\+)").Select(m => m.Groups[1].Value);
                keys.AddRange(call.Groups[1].Value == "Count" ? literals.SelectMany(k => new[] { k + "One", k + "Many" }) : literals);
            }

            // The lengths of "do not disturb": ("30", "Dnd30").
            keys.AddRange(Regex.Matches(text, @"\(""\w+"", ""(Dnd\w+)""\)").Select(m => m.Groups[1].Value));
        }
        return keys;
    }

    [Fact]
    public void FrenchTranslation_HasExactlyTheEnglishKeys() =>
        Assert.Equal(ReadStrings("Strings.resx").Keys.Order(), ReadStrings("Strings.fr.resx").Keys.Order());

    [Fact]
    public void Translations_UseTheSamePlaceholders()
    {
        var english = ReadStrings("Strings.resx");
        var french = ReadStrings("Strings.fr.resx");
        foreach (var (key, text) in english)
            Assert.True(Placeholders(text).SetEquals(Placeholders(french[key])), $"Placeholders differ for {key}");
    }

    [Fact]
    public void EveryKeyUsedByTheApp_Exists()
    {
        var english = ReadStrings("Strings.resx");
        var missing = UsedKeys().Distinct().Where(k => !english.ContainsKey(k)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void DefaultTexts_Exist()
    {
        var english = ReadStrings("Strings.resx");
        Assert.All(Enumerable.Range(1, 4), i => Assert.Contains("DefaultMessage" + i, english.Keys));
        Assert.All(Enumerable.Range(1, 3), i => Assert.Contains("DefaultReply" + i, english.Keys));
    }
}
