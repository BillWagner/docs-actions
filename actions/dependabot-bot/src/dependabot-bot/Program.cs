﻿if (args is { Length: 0 } || args[0] is not string path)
{
    WriteLine("Must specify a repo root directory as input");
    return 1;
}

string destinationFilePath = args is { Length: 2 }
    && !string.IsNullOrWhiteSpace(args[1])
    ? args[1]
    : $".github{Path.AltDirectorySeparatorChar}dependabot.yml";

static void WriteLineToBufferAndOutput(StringBuilder buffer, string content, bool isLimitReached)
{
    if (isLimitReached)
    {
        WriteLine("LIMIT REACHED, OVERFLOW IS DISCARDED!");
        WriteLine(content);
    }
    else
    {
        buffer.AppendLine(content);
        WriteLine(content);
    }
}

/*
Dependabot encountered the following error when parsing your .github/dependabot.yml:

The property '#/updates' had more items than the allowed 200
Please update the config file to conform with Dependabot's specification. 
*/
const int UpdateNodeLimit = 200;
var updateNodeCount = 0;

StringBuilder buffer = new();

// yaml top-matter
string topMatter = """
    # generated by dependadotnet
    # https://github.com/dotnet/core/tree/main/samples/dependadotnet
    version: 2
    updates:
    """;

WriteLineToBufferAndOutput(buffer, topMatter, false);

// Entry to update GitHub Actions
string githubActions = """
    - package-ecosystem: "github-actions" # Core GitHub Actions
      directory: "/"
      schedule:
        interval: "weekly"
        day: "wednesday"
      open-pull-requests-limit: 10
  """;

WriteLineToBufferAndOutput(buffer, githubActions, UpdateNodeLimit == updateNodeCount++);

/* Generate the following pattern for each project file:

  Note: Wednesday was chosen for quick response to .NET patch Tuesday updates

- package-ecosystem: ""nuget""
  directory: ""/"" #projectfilename
  schedule:
      interval: ""weekly""
      day: ""wednesday""
  open-pull-requests-limit: 5
*/

Dictionary<string, string[]> packageIgnore = await GetPackagesInfoAsync();

string dotnetDir = $"**/{Path.AltDirectorySeparatorChar}.dotnet";

Matcher projectMatcher = new();
projectMatcher.AddIncludePatterns(
    new[] { "**/*.csproj", "**/*.fsproj", "**/*.vbproj" });
projectMatcher.AddExclude(dotnetDir);

var patternMatchingResult = projectMatcher.Execute(
    new DirectoryInfoWrapper(
        new DirectoryInfo(path)));

if (patternMatchingResult.HasMatches)
{
    foreach (var fileMatch in patternMatchingResult.Files)
    {
        string file = Path.Combine(path, fileMatch.Path);
        string filename = Path.GetFileName(file);
        string? parentDir = Path.GetDirectoryName(file);
        string relativeDir = parentDir?[path.Length..].Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? Path.AltDirectorySeparatorChar.ToString();
        string? targetFramework = null;
        bool match = false;
        List<PackageIgnoreMapping> mappings = new();
        foreach (string content in File.ReadLines(file))
        {
            if (targetFramework is null && TryGetTargetFramework(content, out targetFramework))
            {
            }

            if (PackageReferenceVersionRegex().IsMatch(content))
            {
                match = true;

                if (TryGetPackageName(content, out string? packageName) &&
                    packageIgnore.TryGetValue($"{packageName}_{targetFramework}", out string[]? ignore))
                {
                    mappings.Add(new(packageName, ignore));
                }

                break;
            }
        }

        if (!match)
        {
            continue;
        }

        WriteLineToBufferAndOutput(buffer, $"""
              - package-ecosystem: "nuget"
                directory: "{relativeDir}" #{filename}
                schedule:
                  interval: "weekly"
                  day: "wednesday"
                open-pull-requests-limit: 5
            """,
            UpdateNodeLimit == updateNodeCount++);

        if (mappings.Count is 0)
        {
            continue;
        }

        /* Format:
    ignore:
     - dependency-name: "Microsoft.AspNetCore.Mvc.NewtonsoftJson"
       versions: ["5.*"]        
        */

        WriteLineToBufferAndOutput(buffer, "    ignore:", false);

        foreach (PackageIgnoreMapping mapping in mappings)
        {
            WriteLineToBufferAndOutput(buffer, $"""
                      - dependency-name: ""{mapping.PackageName}""
                       versions: {PrintArrayAsYaml(mapping.Ignore)}
                """, false);
        }
    }
}

if (buffer is { Length: > 118 /* top matter length */ })
{
    var contents = buffer.ToString();
    await File.WriteAllTextAsync(destinationFilePath, contents);
}

return 0;

static bool TryGetTargetFramework(
    string content,
    [NotNullWhen(true)] out string? targetFramework) =>
    TryGetRegexGroupValue(
        TargetFrameworkRegex(),
        content, "tfm", out targetFramework);

static bool TryGetPackageName(
    string content,
    [NotNullWhen(true)] out string? packageName) =>
    TryGetRegexGroupValue(
        PackageReferenceIncludeRegex(),
        content, "nuget", out packageName);

static bool TryGetRegexGroupValue(
    Regex regex, string content, string groupKey, [NotNullWhen(true)] out string? groupValue)
{
    var match = regex.Match(content);
    if (match is { Success: true } and { Groups.Count: > 0 })
    {
        groupValue = match.Groups[groupKey].Value;
        return true;
    }
    else
    {
        groupValue = null;
        return false;
    }
}

static async Task<Dictionary<string, string[]>> GetPackagesInfoAsync()
{
    Assembly assembly = typeof(Program).Assembly;
    string fileName = $"{assembly.GetName().Name}.packages-ignore.json";
    string? resourceName = assembly.GetManifestResourceNames().FirstOrDefault(name => name == fileName);
    if (resourceName is null)
    {
        return new();
    }

    using var stream = assembly.GetManifestResourceStream(resourceName);
    using var reader = new StreamReader(stream!);
    var json = await reader.ReadToEndAsync();
    JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    PackageInfoSet? packages = JsonSerializer.Deserialize<PackageInfoSet>(json, options);
    return packages switch
    {
        null => throw new IOException("Could not download packages information"),
        _ => packages.Packages
                .SelectMany(package => package.Mapping.Select(mapping => (Key: $"{package.Name}_{mapping.TargetFramework}", Value: mapping.Ignore)))
                .ToDictionary(_ => _.Key, _ => _.Value)
    };
}

static string PrintArrayAsYaml(string[] array)
{
    StringBuilder buffer = new();
    buffer.Append('[');
    for (int i = 0; i < array.Length; i++)
    {
        buffer.Append($@"""{array[i]}""");

        if (i + 1 < array.Length)
        {
            buffer.Append(", ");
        }
    }
    buffer.Append(']');

    return buffer.ToString();
}

file record PackageInfoSet(PackageInfo[] Packages);

file readonly record struct PackageInfo(string Name, PackageTargetFrameworkIgnoreMapping[] Mapping);
file readonly record struct PackageTargetFrameworkIgnoreMapping(string TargetFramework, string[] Ignore);
file readonly record struct PackageIgnoreMapping(string PackageName, string[] Ignore);

static partial class Program
{
    [GeneratedRegex("PackageReference.*Version=\"[0-9]")]
    private static partial Regex PackageReferenceVersionRegex();

    [GeneratedRegex("TargetFramework(.*)>(?<tfm>.+?)</")]
    private static partial Regex TargetFrameworkRegex();

    [GeneratedRegex("<PackageReference(?:.+?)Include=\"\"(?<nuget>.+?)\"\"")]
    private static partial Regex PackageReferenceIncludeRegex();

    private static readonly HttpClient s_client = new();
}