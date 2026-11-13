using Arrow.Common.Analysis;
using Arrow.DataAccess.Build;

namespace Arrow.DataAccess.Tests.Build;

/// <summary>
/// Readers of Maven and Gradle output (P01 §4.3–4.7). Parity tests rewritten from
/// <c>ARROW_Framework/tests/Arrow.Service.Tests/Block1Tests.cs</c> keep the old inputs and expected values.
/// </summary>
public class BuildReadersTests
{
    private const string EffectivePom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <projects>
          <project xmlns="http://maven.apache.org/POM/4.0.0">
            <groupId>com.acme</groupId><artifactId>shop-api</artifactId>
            <properties><maven.compiler.source>1.8</maven.compiler.source></properties>
            <build><sourceDirectory>/ws/b/shop-api/src/main/java</sourceDirectory><directory>/ws/b/shop-api/target</directory></build>
          </project>
          <project xmlns="http://maven.apache.org/POM/4.0.0">
            <groupId>com.acme</groupId><artifactId>shop-core</artifactId>
            <properties><java.version>11</java.version><maven.compiler.source>${java.version}</maven.compiler.source></properties>
            <dependencies>
              <dependency><groupId>com.acme</groupId><artifactId>shop-api</artifactId><version>1.0</version></dependency>
              <dependency><groupId>junit</groupId><artifactId>junit</artifactId><version>4.13.2</version><scope>test</scope></dependency>
            </dependencies>
            <build>
              <sourceDirectory>/ws/b/shop-core/src/main/java</sourceDirectory>
              <testSourceDirectory>/ws/b/shop-core/src/test/java</testSourceDirectory>
              <directory>/ws/b/shop-core/target</directory>
              <plugins><plugin><artifactId>maven-compiler-plugin</artifactId><configuration><release>17</release></configuration></plugin></plugins>
            </build>
          </project>
        </projects>
        """;

    // Parity: Block1Tests.ReadsEffectivePomOfTargetModule
    [Fact]
    public void ReadsEffectivePomOfTargetModule()
    {
        var projects = EffectivePomReader.Parse(EffectivePom);
        Assert.Equal(2, projects.Count);
        var core = EffectivePomReader.SelectTarget(projects, "/ws/b/shop-core")!;
        Assert.Equal("shop-core", core.ArtifactId);
        // The uninterpolated maven.compiler.source (${…}) is skipped, so the release of the plugin wins.
        Assert.Equal("17", core.Declared);
        Assert.Equal("maven-compiler-plugin.release", core.DeclaredSource);
        Assert.Equal("/ws/b/shop-core/src/test/java", core.TestSourceDirectory);
        Assert.Equal(["compile", "test"], core.Dependencies.Select(d => d.Scope));
        Assert.Equal("shop-api", EffectivePomReader.SelectTarget(projects, "/ws/b/shop-api")!.ArtifactId);
        Assert.Equal("1.8", EffectivePomReader.SelectTarget(projects, "/ws/b/shop-api")!.Declared);
    }

    [Fact]
    public void EffectivePomReadsSingleProjectDeclaredOrderAndSurefirePatterns()
    {
        const string pom = """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <artifactId>lib</artifactId>
              <properties><maven.compiler.release>11</maven.compiler.release><maven.compiler.source>1.8</maven.compiler.source></properties>
              <build>
                <plugins>
                  <plugin><artifactId>maven-surefire-plugin</artifactId>
                    <configuration>
                      <includes><include>**/*Spec.java</include><include>%regex[.*IT.*]</include></includes>
                      <excludes><exclude>**/Slow*.java</exclude></excludes>
                    </configuration>
                  </plugin>
                </plugins>
              </build>
            </project>
            """;
        var project = Assert.Single(EffectivePomReader.Parse(pom));
        Assert.Equal(("11", "maven.compiler.release"), (project.Declared, project.DeclaredSource));
        Assert.Equal(["**/*Spec.java", "%regex[.*IT.*]"], project.TestIncludes);
        Assert.Equal(["**/Slow*.java"], project.TestExcludes);

        // Not configured: null, so the caller uses the Surefire defaults.
        var plain = Assert.Single(EffectivePomReader.Parse("<project><artifactId>x</artifactId></project>"));
        Assert.Null(plain.TestIncludes);
        Assert.Null(plain.Declared);
        Assert.Equal(["**/Test*.java", "**/*Test.java", "**/*Tests.java", "**/*TestCase.java"], EffectivePomReader.DefaultSurefireIncludes);
    }

    // Parity: Block1Tests.ParsesDependencyListLines
    [Fact]
    public void ParsesDependencyListLines()
    {
        const string text = """

            The following files have been resolved:
               org.slf4j:slf4j-api:jar:1.7.30:compile:/Users/a/.m2/repository/org/slf4j/slf4j-api/1.7.30/slf4j-api-1.7.30.jar -- module org.slf4j [auto]
               junit:junit:jar:4.13.2:test:C:\Users\a\.m2\repository\junit\junit\4.13.2\junit-4.13.2.jar
               io.netty:netty-tcnative:jar:linux-x86_64:2.0.0:runtime (optional)
               com.acme:shop-api:jar:1.0:compile
               none
            """;
        var deps = MavenDependencyList.Parse(text);
        Assert.Equal(4, deps.Count);
        Assert.Equal(new ResolvedDependency("org.slf4j", "slf4j-api", "1.7.30", "compile", "/Users/a/.m2/repository/org/slf4j/slf4j-api/1.7.30/slf4j-api-1.7.30.jar"), deps[0]);
        Assert.Equal(@"C:\Users\a\.m2\repository\junit\junit\4.13.2\junit-4.13.2.jar", deps[1].Path);
        Assert.Equal(("2.0.0", "runtime"), (deps[2].Version, deps[2].Scope));
        Assert.Null(deps[3].Path);
    }

    [Fact]
    public void DependencyPathMayContainSpaces()
    {
        // Differs from ARROW_Framework, which dropped every line with a space: a user folder such as "Ann Lee" is common on Windows.
        var dep = Assert.Single(MavenDependencyList.Parse(@"   junit:junit:jar:4.13.2:test:C:\Users\Ann Lee\.m2\repository\junit\junit\4.13.2\junit-4.13.2.jar"));
        Assert.Equal(@"C:\Users\Ann Lee\.m2\repository\junit\junit\4.13.2\junit-4.13.2.jar", dep.Path);
        Assert.Empty(MavenDependencyList.Parse("[INFO] Building shop core 1.0 : jar : x : y"));
    }

    // Parity: Block1Tests.ParsesGradleProbeOutput, plus the test task filters of P01 §4.7.
    [Fact]
    public void ParsesGradleProbeOutput()
    {
        const string output = """
            > Task :core:arrowInfo
            ARROW|gradle_version|8.5
            ARROW|project_dir|/ws/b/core
            ARROW|project|:|/ws/b
            ARROW|project|:core|/ws/b/core
            ARROW|java_declared|sourceCompatibility|1.8
            ARROW|test_src|/ws/b/core/src/test/java
            ARROW|dep|testImplementation|org.junit.jupiter|junit-jupiter|5.10.0
            ARROW|dep|compileOnly|org.projectlombok|lombok|1.18.30
            ARROW|dep|testCompile|junit|junit|4.12
            ARROW|dep|compile|com.google.guava|guava|20.0
            ARROW|test_include|**/*Test.class
            ARROW|test_exclude|**/Slow*
            ARROW|resolved|org.junit.jupiter|junit-jupiter-api|5.10.0|/g/junit-jupiter-api-5.10.0.jar
            ARROW|cp|/ws/b/api/build/libs/api.jar
            BUILD SUCCESSFUL
            """;
        var info = GradleProbe.Parse(output);
        Assert.Equal("8.5", info.GradleVersion);
        Assert.Equal("/ws/b/core", info.ProjectDir);
        Assert.Equal(2, info.Projects.Count);
        Assert.Equal(("1.8", "sourceCompatibility"), (info.Declared, info.DeclaredSource));
        Assert.Equal(["test", "provided", "test", "compile"], info.Dependencies.Select(d => d.Scope));
        Assert.Equal("junit-jupiter-api", Assert.Single(info.Resolved).Artifact);
        Assert.Equal(["/ws/b/api/build/libs/api.jar"], info.Classpath);
        Assert.Equal(["**/*Test.class"], info.TestIncludes);
        Assert.Equal(["**/Slow*"], info.TestExcludes);
    }

    // Parity: Block1Tests.GradleProjectFromTask (only GradleProbe.Task; the manifest cases are gone).
    [Fact]
    public void GradleTaskPaths()
    {
        Assert.Equal(":core:test", GradleProbe.Task(":core", "test"));
        Assert.Equal(":test", GradleProbe.Task(":", "test"));
        Assert.Equal(":test", GradleProbe.Task(null, "test"));
        Assert.Contains("arrowInfo", GradleProbe.InitScript, StringComparison.Ordinal);
        Assert.Contains("test_include", GradleProbe.InitScript, StringComparison.Ordinal);
    }
}
