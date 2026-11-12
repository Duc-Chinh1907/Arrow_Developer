using System.Reflection;

namespace Arrow.Common.Errors;

/// <summary>
/// Every error code of the product, by prefix (P00 §6). Each code has a message and a hint in
/// <c>ErrorCatalog.&lt;lang&gt;.json</c>; <c>ErrorCatalogTests</c> checks that the two lists match.
/// Research-only codes of the team specs (P00 §7) are deliberately absent.
/// </summary>
public static class ErrorCodes
{
    // Block 1: project analysis (P01 §9)
    public const string B1BuildRootNotFound = "B1_BUILD_ROOT_NOT_FOUND";
    public const string B1UrlHasCredentials = "B1_URL_HAS_CREDENTIALS";
    public const string B1AuthRequired = "B1_AUTH_REQUIRED";
    public const string B1CloneFailed = "B1_CLONE_FAILED";
    public const string B1CommitMissing = "B1_COMMIT_MISSING";
    public const string B1CutMissing = "B1_CUT_MISSING";
    public const string B1CutAmbiguous = "B1_CUT_AMBIGUOUS";
    public const string B1CutNotTestable = "B1_CUT_NOT_TESTABLE";
    public const string B1ModuleMismatch = "B1_MODULE_MISMATCH";
    public const string B1BuildToolMissing = "B1_BUILD_TOOL_MISSING";
    public const string B1JdkMissing = "B1_JDK_MISSING";
    public const string B1DependencyResolution = "B1_DEPENDENCY_RESOLUTION";
    public const string B1BaselineBuildFailed = "B1_BASELINE_BUILD_FAILED";
    public const string B1BaselineTimeout = "B1_BASELINE_TIMEOUT";
    public const string B1TestNameExhausted = "B1_TEST_NAME_EXHAUSTED";
    public const string B1Workspace = "B1_WORKSPACE";

    // Block 2: generation (P02 §7) and LLM (P03 §11)
    public const string B2InvalidOutput = "B2_INVALID_OUTPUT";
    public const string B2TemplateInvalid = "B2_TEMPLATE_INVALID";
    public const string B2LlmUnavailable = "B2_LLM_UNAVAILABLE";
    public const string B2LlmConfig = "B2_LLM_CONFIG";
    public const string B2WrongModel = "B2_WRONG_MODEL";
    public const string B2TokenBudget = "B2_TOKEN_BUDGET";

    // Block 3: verification and repair (P04 §10)
    public const string B3Timeout = "B3_TIMEOUT";
    public const string B3Dependency = "B3_DEPENDENCY";
    public const string B3ToolError = "B3_TOOL_ERROR";
    public const string B3Workspace = "B3_WORKSPACE";

    // Block 4: quality (P05 §8)
    public const string B4SuiteCompile = "B4_SUITE_COMPILE";
    public const string B4JacocoFailed = "B4_JACOCO_FAILED";
    public const string B4PitFailed = "B4_PIT_FAILED";
    public const string B4PitScope = "B4_PIT_SCOPE";
    public const string B4TsDetectFailed = "B4_TSDETECT_FAILED";

    // Writing into the project (P06 §7)
    public const string WTargetOutsideProject = "W_TARGET_OUTSIDE_PROJECT";
    public const string WProjectChanged = "W_PROJECT_CHANGED";
    public const string WReverifyFailed = "W_REVERIFY_FAILED";
    public const string WTargetExists = "W_TARGET_EXISTS";
    public const string WIo = "W_IO";

    // Config, secrets, storage and tools (P09 §9)
    public const string CfgHomePath = "CFG_HOME_PATH";
    public const string CfgParse = "CFG_PARSE";
    public const string CfgInvalid = "CFG_INVALID";
    public const string CfgNewerSchema = "CFG_NEWER_SCHEMA";
    public const string CfgSecretInProject = "CFG_SECRET_IN_PROJECT";
    public const string CfgNoModel = "CFG_NO_MODEL";
    public const string CfgSecretMissing = "CFG_SECRET_MISSING";
    public const string CfgToolMissing = "CFG_TOOL_MISSING";
    public const string CfgToolChecksum = "CFG_TOOL_CHECKSUM";
    public const string CfgDiskFull = "CFG_DISK_FULL";

    // java-helper (P10 §6)
    public const string JhJdkMissing = "JH_JDK_MISSING";
    public const string JhStartFailed = "JH_START_FAILED";
    public const string JhTimeout = "JH_TIMEOUT";
    public const string JhVersionMismatch = "JH_VERSION_MISMATCH";
    public const string JhInputTooLarge = "JH_INPUT_TOO_LARGE";

    // Use cases (P07 §12)
    public const string GenNoTargets = "GEN_NO_TARGETS";
    public const string GenResumeMismatch = "GEN_RESUME_MISMATCH";
    public const string GenResumeNotFound = "GEN_RESUME_NOT_FOUND";

    // CLI (P08 §10)
    public const string CliArgs = "CLI_ARGS";
    public const string CliNoTty = "CLI_NO_TTY";
    public const string CliInternal = "CLI_INTERNAL";

    /// <summary>All codes above, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = typeof(ErrorCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToArray();
}
