using Arrow.Business.Quality;

namespace Arrow.Business.Tests.Quality;

/// <summary>Reading the tool results and the PIT setup of P05 §10. Parity with Block4Tests of ARROW_Framework (tool part).</summary>
public class ResultParserTests
{
    private const string JacocoCsv = """
        GROUP,PACKAGE,CLASS,INSTRUCTION_MISSED,INSTRUCTION_COVERED,BRANCH_MISSED,BRANCH_COVERED,LINE_MISSED,LINE_COVERED,COMPLEXITY_MISSED,COMPLEXITY_COVERED,METHOD_MISSED,METHOD_COVERED
        app,com.acme.shop,Order,10,40,4,6,5,20,3,7,1,6
        app,com.acme.shop,Order.Item,2,8,0,0,1,4,0,2,0,2
        app,com.acme.shop,OrderService,100,0,10,0,30,0,10,0,8,0
        app,com.acme.other,Order,50,0,2,0,9,0,1,0,1,0
        """;

    // Parity: Block4Tests.JacocoTakesCutAndNestedClassesOnly
    [Fact]
    public void JacocoTakesCutAndNestedClassesOnly()
    {
        var coverage = JacocoCsvParser.Parse(JacocoCsv, "com.acme.shop.Order")!;
        Assert.Equal(new Dictionary<string, int>
        {
            ["LINE_COVERED"] = 24, ["LINE_MISSED"] = 6, ["BRANCH_COVERED"] = 6, ["BRANCH_MISSED"] = 4, ["METHOD_COVERED"] = 8, ["METHOD_MISSED"] = 1,
        }, coverage.Counters);
        Assert.Equal((0.8, 0.6, 0.8889), (coverage.Line!.Value, coverage.Branch!.Value, coverage.Method!.Value));

        // A CUT without branches has branch null, not 0; a CUT missing from the report gives null.
        Assert.Null(JacocoCsvParser.Parse(JacocoCsv.Replace("4,6,5,20", "0,0,5,20", StringComparison.Ordinal), "com.acme.shop.Order")!.Branch);
        Assert.Null(JacocoCsvParser.Parse(JacocoCsv, "com.acme.shop.Missing"));
        Assert.Null(JacocoCsvParser.Parse("", "com.acme.shop.Order"));
        Assert.Null(JacocoCsvParser.Parse("A,B\n1,2", "com.acme.shop.Order"));
    }

    [Fact]
    public void JacocoReadsTheDefaultPackage()
    {
        var coverage = JacocoCsvParser.Parse("GROUP,PACKAGE,CLASS,LINE_MISSED,LINE_COVERED\r\napp,,Calc,1,3\r\napp,,CalcTest,9,9\r\n", "Calc")!;
        Assert.Equal(0.75, coverage.Line);
        Assert.Null(coverage.Branch);
    }

    private static string M(string status, string cls = "com.acme.shop.Order", string method = "total", int line = 31, string mutator = "org.pitest.mutationtest.engine.gregor.mutators.ConditionalsBoundaryMutator") =>
        $"<mutation detected='{status is "KILLED" or "TIMED_OUT"}' status='{status}'><sourceFile>Order.java</sourceFile><mutatedClass>{cls}</mutatedClass><mutatedMethod>{method}</mutatedMethod><lineNumber>{line}</lineNumber><mutator>{mutator}</mutator></mutation>";

    // Parity: Block4Tests.PitCountsStatusesAndDetectsOutOfScopeMutants, plus the survivors sample of P05 §5.
    [Fact]
    public void PitCountsStatusesAndDetectsOutOfScopeMutants()
    {
        var statuses = Enumerable.Repeat("KILLED", 30).Concat(Enumerable.Repeat("SURVIVED", 12)).Concat(Enumerable.Repeat("NO_COVERAGE", 10)).Concat(Enumerable.Repeat("TIMED_OUT", 2));
        var xml = "<?xml version=\"1.0\"?><mutations>" + string.Concat(statuses.Select((s, i) => M(s, i == 0 ? "com.acme.shop.Order$Item" : "com.acme.shop.Order", line: i))) + "</mutations>";
        var (result, outOfScope) = PitXmlParser.Parse(xml, "com.acme.shop.Order");

        // P05 §5: score = (30 + 2) / 54, test strength = 30 / (54 − 10).
        Assert.Equal((54, 30, 12, 10, 2, 0, 0), (result.Total, result.Killed, result.Survived, result.NoCoverage, result.TimedOut, result.MemoryError, result.RunError));
        Assert.Equal(0.5926, result.Score);
        Assert.Equal(0.6818, result.TestStrength);
        Assert.Empty(outOfScope);
        Assert.Equal(PitXmlParser.MaxSurvivors, result.SurvivorsSample.Count);
        Assert.Equal(("total", 30, "ConditionalsBoundaryMutator"), (result.SurvivorsSample[0].Method, result.SurvivorsSample[0].Line!.Value, result.SurvivorsSample[0].Mutator));

        var leaked = PitXmlParser.Parse("<mutations>" + M("KILLED", "com.acme.shop.OrderService") + "</mutations>", "com.acme.shop.Order");
        Assert.Equal(["com.acme.shop.OrderService"], leaked.OutOfScope);
        var empty = PitXmlParser.Parse("<mutations/>", "com.acme.shop.Order").Result;
        Assert.Equal((0, (double?)null, (double?)null), (empty.Total, empty.Score, empty.TestStrength));

        // Only NO_COVERAGE: a score of 0 but no test strength.
        var uncovered = PitXmlParser.Parse("<mutations>" + M("NO_COVERAGE") + "</mutations>", "com.acme.shop.Order").Result;
        Assert.Equal((0.0, (double?)null), (uncovered.Score!.Value, uncovered.TestStrength));
    }

    // Parity: Block4Tests.TsDetectSmellsAndDensity
    [Fact]
    public void TsDetectSmellsAndDensity()
    {
        const string csv = """
            App,TestClass,TestFilePath,ProductionFilePath,RelativeTestFilePath,RelativeProductionFilePath,NumberOfMethods,Assertion Roulette,Conditional Test Logic,Magic Number Test,Empty Test
            app,OrderService_ArrowTest,/t/OrderService_ArrowTest.java,/m/OrderService.java,,,5,3,0,1,false
            """;
        var smells = TsDetectCsvParser.Parse(csv, 5)!;
        Assert.Equal(new Dictionary<string, int> { ["Assertion Roulette"] = 3, ["Magic Number Test"] = 1 }, smells.Smells);
        Assert.Equal((4, 0.8), (smells.TotalSmells, smells.Density!.Value));
        Assert.Null(TsDetectCsvParser.Parse(csv, 0)!.Density);
        Assert.Equal(1, TsDetectCsvParser.Parse(csv.Replace(",false", ",true", StringComparison.Ordinal), 5)!.Smells["Empty Test"]);
        Assert.Null(TsDetectCsvParser.Parse("App,TestClass\n", 5));
    }

    [Fact]
    public void CsvKeepsQuotedCommas() =>
        Assert.Equal(["a", "b,c", "say \"hi\"", ""], Csv.Split("a,\"b,c\",\"say \"\"hi\"\"\","));

    // Parity: Block4Tests.PlatformVersionFromClasspath
    [Fact]
    public void PlatformVersionFromClasspath()
    {
        Assert.Equal("1.10.2", PitSetup.PlatformVersion(["/m2/org/junit/platform/junit-platform-engine-1.10.2.jar", "/m2/junit-jupiter-api-5.10.2.jar"]));
        Assert.Equal("1.9.3", PitSetup.PlatformVersion(["/m2/junit-platform-commons-1.9.3.jar"]));
        Assert.Null(PitSetup.PlatformVersion(["/m2/junit-4.13.2.jar"]));
    }

    // P05 §10: the PIT classpath for JUnit 4, JUnit 5 with and without the launcher, and TestNG.
    [Fact]
    public void PitClasspathForEachFramework()
    {
        string[] junit4 = ["/m2/junit-4.13.2.jar", "/m2/hamcrest-core-1.3.jar", "/w/target/classes"];
        var cp4 = PitSetup.Classpath("/w/target/classes", "/w/target/test-classes", junit4, []);
        Assert.Equal(["/w/target/classes", "/w/target/test-classes", "/m2/junit-4.13.2.jar", "/m2/hamcrest-core-1.3.jar"], cp4);
        Assert.Null(PitSetup.PluginTool("junit4"));
        Assert.Null(PitSetup.PluginTool(null));
        Assert.Equal(["/pit/pitest.jar"], PitSetup.LaunchClasspath(["/pit/pitest.jar"], cp4));

        string[] junit5 = ["/m2/junit-jupiter-api-5.10.2.jar", "/m2/junit-platform-engine-1.10.2.jar", "/m2/opentest4j-1.3.0.jar"];
        Assert.False(PitSetup.HasLauncher(junit5));
        Assert.Equal("pitest-junit5-plugin", PitSetup.PluginTool("junit5"));
        var cp5 = PitSetup.Classpath("/c", "/t", junit5, ["/tools/junit-platform/junit-platform-launcher-1.10.2.jar"]);
        Assert.Equal("/tools/junit-platform/junit-platform-launcher-1.10.2.jar", cp5[^1]);
        Assert.True(PitSetup.HasLauncher(cp5));
        Assert.Equal(["/pit/pitest.jar", "/pit/plugin.jar", "/m2/junit-jupiter-api-5.10.2.jar", "/m2/junit-platform-engine-1.10.2.jar", "/m2/opentest4j-1.3.0.jar",
            "/tools/junit-platform/junit-platform-launcher-1.10.2.jar"], PitSetup.LaunchClasspath(["/pit/pitest.jar", "/pit/plugin.jar"], cp5));

        Assert.Equal("pitest-testng-plugin", PitSetup.PluginTool("testng"));
        Assert.Equal(["/pit/pitest.jar", "/m2/testng-7.10.2.jar", "/m2/jcommander-1.82.jar"],
            PitSetup.LaunchClasspath(["/pit/pitest.jar"], PitSetup.Classpath("/c", "/t", ["/m2/testng-7.10.2.jar", "/m2/jcommander-1.82.jar", "/m2/slf4j-api.jar"], [])));
    }

    // P05 §5 step 2.
    [Theory]
    [InlineData(17, "/jdk17", "/jdk17")]
    [InlineData(11, "/jdk11", "/jdk11")]
    [InlineData(8, "/jdk8", "/jdk11")]
    public void PitUsesTheProjectJdkFrom11OrTheSmallestNewerOne(int project, string projectHome, string expected) =>
        Assert.Equal(expected, PitSetup.ChooseJdk(project, projectHome, new Dictionary<int, string> { [8] = "/jdk8", [21] = "/jdk21", [11] = "/jdk11", [17] = "/jdk17" }));

    [Fact]
    public void WithoutAJdk11PitHasNone() =>
        Assert.Null(PitSetup.ChooseJdk(8, "/jdk8", new Dictionary<int, string> { [8] = "/jdk8" }));

    [Fact]
    public void ScopesUseTheTwoExactPatterns()
    {
        var args = PitSetup.Arguments("com.myshop.Order", "com.myshop.Order_ArrowTest", "/r", "/cp.txt", "/src");
        Assert.Equal("com.myshop.Order,com.myshop.Order$*", args[args.ToList().IndexOf("--targetClasses") + 1]);
        Assert.Equal("com.myshop.Order_ArrowTest", args[args.ToList().IndexOf("--targetTests") + 1]);
        Assert.Contains("--failWhenNoMutations=false", args);
        Assert.Equal("-javaagent:/a.jar=destfile=/x.exec,append=true,includes=com.myshop.Order:com.myshop.Order$* -Djdk.attach.allowAttachSelf=true",
            JacocoSetup.AgentOption("/a.jar", "/x.exec", "com.myshop.Order"));
    }
}
