using Arrow.Common.Analysis;

namespace Arrow.DataAccess.Build;

/// <summary>What the init script prints about one Gradle project (P01 §4.3–4.7).</summary>
/// <param name="GradleVersion">Version of Gradle; empty when not printed.</param>
/// <param name="ProjectDir">Absolute folder of the project the task ran in.</param>
/// <param name="Projects">Every project of the build: path (<c>:core</c>) and absolute folder.</param>
/// <param name="Declared">Declared Java level; null when none.</param>
/// <param name="DeclaredSource"><c>toolchain</c>, <c>release</c> or <c>sourceCompatibility</c>.</param>
/// <param name="MainSourceDirs">Absolute <c>sourceSets.main.java.srcDirs</c>.</param>
/// <param name="TestSourceDirs">Absolute <c>sourceSets.test.java.srcDirs</c>.</param>
/// <param name="Dependencies">Declared dependencies, with a Maven-like scope.</param>
/// <param name="TestIncludes">Include patterns of the <c>test</c> task; empty means every class.</param>
/// <param name="TestExcludes">Exclude patterns of the <c>test</c> task.</param>
/// <param name="Resolved">Resolved artifacts of <c>testRuntimeClasspath</c>.</param>
/// <param name="Classpath">Files of <c>testRuntimeClasspath</c>.</param>
public sealed record GradleInfo(
    string GradleVersion,
    string ProjectDir,
    IReadOnlyList<GradleProject> Projects,
    string? Declared,
    string? DeclaredSource,
    IReadOnlyList<string> MainSourceDirs,
    IReadOnlyList<string> TestSourceDirs,
    IReadOnlyList<DependencyRef> Dependencies,
    IReadOnlyList<string> TestIncludes,
    IReadOnlyList<string> TestExcludes,
    IReadOnlyList<ResolvedDependency> Resolved,
    IReadOnlyList<string> Classpath);

/// <summary>A Gradle project: path such as <c>:core</c> and absolute folder.</summary>
public sealed record GradleProject(string Path, string Dir);

/// <summary>
/// The init script <c>arrow-info.gradle</c> and the reader of its output (P01 §4.3–4.7, §6).
/// Every line it prints starts with <c>ARROW|</c>, so the rest of the Gradle output is ignored.
/// </summary>
public static class GradleProbe
{
    /// <summary>File name of the init script.</summary>
    public const string FileName = "arrow-info.gradle";

    /// <summary>Task that prints the configuration of a project.</summary>
    public const string InfoTask = "arrowInfo";

    /// <summary>Task that prints the resolved test classpath; it depends on that classpath, so it runs after the sibling projects are built.</summary>
    public const string ClasspathTask = "arrowClasspath";

    /// <summary>
    /// The init script. It disables the checkstyle and spotless tasks, makes test failures not fail the build
    /// (the baseline must run every test), and adds <see cref="InfoTask"/> and <see cref="ClasspathTask"/>.
    /// Written against the API of Gradle 6 so that it runs on Gradle 6 to 9.
    /// </summary>
    public const string InitScript = """
        def arrowOut = { String key, Object value -> println('ARROW|' + key + '|' + (value == null ? '' : value.toString().replace('\n', ' ').replace('\r', ' '))) }
        def arrowTestClasspath = { p -> p.configurations.findByName('testRuntimeClasspath') }

        allprojects { p ->
            p.tasks.matching { t -> t.name.startsWith('checkstyle') || t.name.startsWith('spotless') }.configureEach { t -> t.enabled = false }
            p.tasks.withType(Test).configureEach { t -> t.ignoreFailures = true }

            p.tasks.register('arrowInfo') {
                doLast {
                    arrowOut('gradle_version', p.gradle.gradleVersion)
                    arrowOut('project_dir', p.projectDir.absolutePath)
                    p.rootProject.allprojects.each { q -> arrowOut('project', q.path + '|' + q.projectDir.absolutePath) }

                    def declared = null
                    def javaExt = p.extensions.findByName('java')
                    try {
                        if (javaExt != null && javaExt.hasProperty('toolchain') && javaExt.toolchain.languageVersion.isPresent()) {
                            declared = 'toolchain|' + javaExt.toolchain.languageVersion.get()
                        }
                    } catch (Throwable ignored) { }
                    if (declared == null) {
                        try {
                            def compile = p.tasks.findByName('compileJava')
                            if (compile != null && compile.options.hasProperty('release') && compile.options.release.isPresent()) {
                                declared = 'release|' + compile.options.release.get()
                            }
                        } catch (Throwable ignored) { }
                    }
                    if (declared == null) {
                        try {
                            if (javaExt != null && javaExt.hasProperty('sourceCompatibility')) { declared = 'sourceCompatibility|' + javaExt.sourceCompatibility }
                            else if (p.hasProperty('sourceCompatibility')) { declared = 'sourceCompatibility|' + p.sourceCompatibility }
                        } catch (Throwable ignored) { }
                    }
                    if (declared != null) { arrowOut('java_declared', declared) }

                    if (p.hasProperty('sourceSets')) {
                        def main = p.sourceSets.findByName('main')
                        def test = p.sourceSets.findByName('test')
                        if (main != null) { main.java.srcDirs.each { d -> arrowOut('main_src', d.absolutePath) } }
                        if (test != null) { test.java.srcDirs.each { d -> arrowOut('test_src', d.absolutePath) } }
                    }

                    ['api', 'implementation', 'compileOnly', 'runtimeOnly',
                     'testImplementation', 'testCompileOnly', 'testRuntimeOnly'].each { n ->
                        def c = p.configurations.findByName(n)
                        if (c != null) {
                            c.dependencies.each { d -> arrowOut('dep', n + '|' + (d.group ?: '') + '|' + d.name + '|' + (d.version ?: '')) }
                        }
                    }

                    def testTask = p.tasks.findByName('test')
                    if (testTask instanceof Test) {
                        testTask.includes.each { i -> arrowOut('test_include', i) }
                        testTask.excludes.each { e -> arrowOut('test_exclude', e) }
                    }
                }
            }

            p.tasks.register('arrowClasspath') {
                dependsOn { def c = arrowTestClasspath(p); c != null ? c : [] }
                doLast {
                    def c = arrowTestClasspath(p)
                    if (c != null) {
                        c.resolvedConfiguration.resolvedArtifacts.each { a ->
                            def id = a.moduleVersion.id
                            arrowOut('resolved', id.group + '|' + id.name + '|' + id.version + '|' + a.file.absolutePath)
                        }
                        c.files.each { f -> arrowOut('cp', f.absolutePath) }
                    }
                }
            }
        }
        """;

    /// <summary>Task path of a project: <c>(":core", "test")</c> → <c>:core:test</c>; the root project gives <c>:test</c>.</summary>
    public static string Task(string? project, string task) =>
        project is null or ":" or "" ? ":" + task : project.TrimEnd(':') + ":" + task;

    /// <summary>Reads the <c>ARROW|</c> lines of a Gradle output; other lines are ignored.</summary>
    public static GradleInfo Parse(string output)
    {
        var projects = new List<GradleProject>();
        var main = new List<string>();
        var test = new List<string>();
        var dependencies = new List<DependencyRef>();
        var includes = new List<string>();
        var excludes = new List<string>();
        var resolved = new List<ResolvedDependency>();
        var classpath = new List<string>();
        string version = "", projectDir = "";
        string? declared = null, declaredSource = null;

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("ARROW|", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split('|');
            var value = parts.Length > 2 ? string.Join('|', parts[2..]) : "";
            switch (parts[1])
            {
                case "gradle_version":
                    version = value;
                    break;
                case "project_dir":
                    projectDir = value;
                    break;
                case "project" when parts.Length >= 4:
                    projects.Add(new GradleProject(parts[2], string.Join('|', parts[3..])));
                    break;
                case "java_declared" when parts.Length >= 4:
                    declaredSource = parts[2];
                    declared = parts[3];
                    break;
                case "main_src":
                    main.Add(value);
                    break;
                case "test_src":
                    test.Add(value);
                    break;
                case "dep" when parts.Length >= 6 && parts[4].Length > 0:
                    dependencies.Add(new DependencyRef(parts[3], parts[4], parts[5], ScopeOf(parts[2])));
                    break;
                case "test_include" when value.Length > 0:
                    includes.Add(value);
                    break;
                case "test_exclude" when value.Length > 0:
                    excludes.Add(value);
                    break;
                case "resolved" when parts.Length >= 6:
                    resolved.Add(new ResolvedDependency(parts[2], parts[3], parts[4], "test", string.Join('|', parts[5..])));
                    break;
                case "cp" when value.Length > 0:
                    classpath.Add(value);
                    break;
            }
        }

        return new GradleInfo(version, projectDir, projects, declared, declaredSource, main, test, dependencies, includes, excludes, resolved, classpath);
    }

    /// <summary>Maven-like scope of a Gradle configuration.</summary>
    private static string ScopeOf(string configuration) => configuration switch
    {
        "compileOnly" => "provided",
        "runtimeOnly" => "runtime",
        _ when configuration.StartsWith("test", StringComparison.Ordinal) => "test",
        _ => "compile",
    };
}
