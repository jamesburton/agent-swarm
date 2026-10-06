namespace Swarm.Tools.Tests.Support;

/// <summary>Locates and builds command lines for the <c>Swarm.FakeSuite</c> test executable.</summary>
public static class FakeSuite
{
    /// <summary>Gets the path of the built <c>Swarm.FakeSuite.dll</c>.</summary>
    public static string DllPath { get; } = Locate();

    /// <summary>Builds the command line that runs the fake suite.</summary>
    /// <param name="extra">Extra arguments passed to the fake suite.</param>
    /// <returns><c>dotnet</c>, the DLL path, then <paramref name="extra"/>.</returns>
    public static IReadOnlyList<string> Command(params string[] extra) => ["dotnet", DllPath, .. extra];

    // tests/Swarm.Tools.Tests/bin/<Configuration>/<tfm>/ -> tests/Swarm.FakeSuite/bin/<Configuration>/<tfm>/Swarm.FakeSuite.dll
    static string Locate()
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
        var configuration = tfmDir.Parent!.Name;
        var testsDir = tfmDir.Parent!.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(testsDir, "Swarm.FakeSuite", "bin", configuration, tfmDir.Name, "Swarm.FakeSuite.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException($"FakeSuite not built at {path}; build tests/Swarm.FakeSuite first", path);
    }
}
