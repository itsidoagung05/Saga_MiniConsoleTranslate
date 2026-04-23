namespace Saga_MiniConsoleTranslate.Services;

internal static class PathResolver
{
    public static string ResolveSolutionRoot()
        => FindSolutionDirectory()
           ?? FindMiniConsoleProjectDirectory()
           ?? Directory.GetCurrentDirectory();

    public static string ResolveForRead(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        foreach (var basePath in EnumerateBasePaths())
        {
            var candidate = Path.GetFullPath(path, basePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return candidate;
        }

        return Path.GetFullPath(path, GetPreferredBasePath());
    }

    public static string ResolveForWrite(string path)
    {
        if (Path.IsPathRooted(path))
            return path;

        return Path.GetFullPath(path, GetPreferredBasePath());
    }

    private static IEnumerable<string> EnumerateBasePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var projectDirectory = FindMiniConsoleProjectDirectory();
        if (!string.IsNullOrWhiteSpace(projectDirectory) && seen.Add(projectDirectory))
            yield return projectDirectory;

        var solutionDirectory = FindSolutionDirectory();
        if (!string.IsNullOrWhiteSpace(solutionDirectory) && seen.Add(solutionDirectory))
            yield return solutionDirectory;

        var currentDirectory = Directory.GetCurrentDirectory();
        if (seen.Add(currentDirectory))
            yield return currentDirectory;

        var appBaseDirectory = AppContext.BaseDirectory;
        if (seen.Add(appBaseDirectory))
            yield return appBaseDirectory;
    }

    private static string GetPreferredBasePath()
    {
        return FindMiniConsoleProjectDirectory()
            ?? FindSolutionDirectory()
            ?? Directory.GetCurrentDirectory();
    }

    private static string? FindMiniConsoleProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var projectFile = Path.Combine(directory.FullName, "Saga_MiniConsoleTranslate.csproj");
            if (File.Exists(projectFile))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindSolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var hasSolution = directory.EnumerateFiles("*.sln", SearchOption.TopDirectoryOnly).Any();
            if (hasSolution)
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
