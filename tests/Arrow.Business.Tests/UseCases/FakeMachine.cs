using Arrow.Business.Environment;
using Arrow.DataAccess.Config;
using Arrow.DataAccess.Processes;
using Arrow.DataAccess.Storage;

namespace Arrow.Business.Tests.UseCases;

/// <summary>A temporary folder with fake JDK homes, build tools and an <c>ARROW_HOME</c>, for the use cases of GĐ2.</summary>
internal sealed class FakeMachine : IDisposable
{
    public FakeMachine()
    {
        Root = Path.Combine(Path.GetTempPath(), "arrow-biz-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Home = new ArrowHome(Path.Combine(Root, "home"));
        Home.EnsureCreated();
        Runner = new FakeRunner();
    }

    public string Root { get; }

    public ArrowHome Home { get; }

    public FakeRunner Runner { get; }

    public Dictionary<string, string?> Environment { get; } = new();

    public UserConfigStore Config => new(Home, () => Environment);

    /// <summary>A tool discovery that sees only this machine: fake folders, no real JDK, git or Maven.</summary>
    public ToolDiscovery Discovery => new(Runner, name => Environment.GetValueOrDefault(name), Path.Combine(Root, "profile"), isWindows: false);

    /// <summary>Creates <c>bin/java</c> and <c>bin/javac</c> (both spellings, so Windows and Unix lookups find them) and registers the version it prints.</summary>
    public string AddJdk(int major, string version, bool withJavac = true)
    {
        var home = Path.Combine(Root, "jdks", "jdk-" + major);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        foreach (var name in withJavac ? new[] { "java", "java.exe", "javac", "javac.exe" } : ["java", "java.exe"])
        {
            File.WriteAllText(Path.Combine(home, "bin", name), "");
        }

        Runner.JavaVersions[home] = version;
        Environment[$"JAVA_HOME_{major}_X64"] = home;
        return home;
    }

    /// <summary>Puts a program on the fake PATH; <paramref name="output"/> is what <c>-v</c> prints.</summary>
    public string AddTool(string name, string output)
    {
        var dir = Path.Combine(Root, "bin");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "");
        Runner.ToolOutputs[path] = output;
        Environment["PATH"] = dir;
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Answers <c>java -version</c>, <c>mvn -v</c>, <c>git --version</c> from tables; records every command.</summary>
    internal sealed class FakeRunner : IProcessRunner
    {
        public Dictionary<string, string> JavaVersions { get; } = new();

        public Dictionary<string, string> ToolOutputs { get; } = new();

        public List<ProcessSpec> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Commands.Add(spec);
            var program = Path.GetFileNameWithoutExtension(spec.FileName);
            if (program == "java")
            {
                var home = Path.GetDirectoryName(Path.GetDirectoryName(spec.FileName))!;
                var version = JavaVersions.GetValueOrDefault(home, "17.0.1");
                var text = version.StartsWith("1.", StringComparison.Ordinal)
                    ? $"java version \"{version}\"\nJava(TM) SE Runtime Environment"
                    : $"openjdk version \"{version}\" 2026-01-01\nOpenJDK Runtime Environment";
                return Task.FromResult(new ProcessResult(0, false, text, TimeSpan.Zero));
            }

            return Task.FromResult(ToolOutputs.TryGetValue(spec.FileName, out var output)
                ? new ProcessResult(output.StartsWith("FAIL:", StringComparison.Ordinal) ? 1 : 0, false, output, TimeSpan.Zero)
                : new ProcessResult(1, false, "not found", TimeSpan.Zero));
        }
    }
}
