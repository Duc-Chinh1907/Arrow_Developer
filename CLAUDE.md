# CLAUDE.md

This file provides guidance to Claude Code when working in this repository.

## Working with Chính

- Chính (GitHub `Duc-Chinh1907`) owns this project and **codes it alone**. Other members of team SET490_G2 only review the docs, watch the milestone demos and may help run the GĐ7 validation.
- Reply in **Vietnamese**, addressing the user as "anh" and referring to yourself as "em".
- Code, identifiers and commit messages are in English. User-facing CLI messages are in Vietnamese (`ui.language = vi`).
- Push to GitHub, create releases or publish packages **only when Chính asks**. The repo `Duc-Chinh1907/Arrow_Developer` is **public** (his choice), so never commit secrets, `runs/`, `work/`, `docs.zip` or machine-specific config.

## What this is

**ARROW Developer CLI** (`arrow`) is an end-user tool for Java developers:
- It analyses a Maven/Gradle project (a local folder or a GitHub URL with a ref).
- It generates a unit test for a class with an LLM.
- It verifies the test in 5 steps on the real build, then repairs it with **Adaptive Repair**.
- It measures JaCoCo, PIT and tsDetect.
- It writes `<CUT>_ArrowTest.java` into the project **only with the user's consent, never overwriting**.

It is the product version of the team's research framework. There is **no research mode**: no manifest, Fixed Repair, EvoSuite, RQ reports, shards or pilot.

## Documents (read before coding a phase)

- `docs/PLAN_CODE.vi.md`: the plan, phases GĐ0–GĐ8 done one after another. **Follow it.**
- `docs/ARCHITECTURE.vi.md`: overview of the 4 blocks and the 3 layers.
- `docs/specs/P00…P11`: the product specs, each standalone.
- `docs/reference/00…05` (`Rxx`): the team's algorithm specs.
  - Precedence: P-spec wins on product matters (input, UI, storage); R-spec wins on algorithms unless the P-spec says "khác R".
  - `reference/` is a **verbatim copy** of `D:\Ky9\DoAn\ReportDoAn\ARROW_Developer\docs\0*.md`, which Chính edits. When he says a team spec changed:
    1. Diff it after normalizing the table padding, because most of the diff is column alignment.
    2. Copy it into `reference/`.
    3. Update the affected P-specs and PLAN.
  - Never copy or cite spec 06 (console UI, dropped) or spec 07 (ignored).

## Decisions already made (do not reopen)

- **.NET 10 LTS.** Projects: `Arrow.Cli → Arrow.Business → Arrow.DataAccess`, all referencing `Arrow.Common`.
  - DataAccess does process, HTTP and file I/O.
  - Business makes the decisions.
  - Progress is reported through `IProgress<ArrowEvent>`; questions to the user go through `IConfirmation`.
- **Write the C# code new, following the P-specs** (decided 12/11/2026, replacing the 10/11 "copy the code" decision; see PLAN §2.1). From the team's implementation `D:\Ky9\DoAn\ReportDoAn\ARROW_Framework` (net8.0, research-oriented):
  - **copy verbatim** only `tools/java-helper` (Java), the product prompts in `prompts/`, `tools/tools.lock.json` (minus EvoSuite) and the 5 fixtures, each in its own commit noting source and date;
  - for the log/report parsers and algorithm classes (`JavacErrorParser`, `TestResultClassifier`, `SignatureBuilder`, `CheckpointComparer`, `AdaptiveRepairPolicy`, `ResultParsers`, `EffectivePomReader`, `GradleProbe` init script…), **rewrite** them and open the old code only for edge cases. Copying a regex or constant table is fine with a `// from ARROW_Framework/<file>` comment; copying whole `.cs` files is not;
  - its C# tests become **parity tests**: rewrite them against the new interfaces, keeping the same inputs and expected outputs;
  - where the old code and a P-spec differ, the P-spec wins; note the difference in the PR;
  - **never modify or build inside `ARROW_Framework`**; copy it to a temp folder first;
  - **do not read** its `litellm.config.local.yaml` or `machine.local.json`, which hold secrets.
- `-Werror` → `UNKNOWN`, exactly as R03 §3.2.
- **Test file name:** `<CUT>_ArrowTest`. On a collision use `<CUT>_Arrow<k>Test` (k = 2…99), ask the user before writing it, and warn when the Surefire includes don't match.
- Tool versions come from `ARROW_Framework/tools/tools.lock.json`, without EvoSuite.
- Repair defaults: N = 5, `early_stop_p` = 3 (must be ≥ 3).
- **Tests use xUnit v3** (decided 12/11/2026, replacing xUnit 2.9.x). A test that lacks a tool or JDK calls `Assert.Skip`/`Assert.SkipUnless`; it never returns early and passes silently.
- The team spec changes of 10/11/2026 are **not in ARROW_Framework**, so the old code and its tests do not cover them:
  - maven-compiler-plugin 2.x javac format (GĐ4);
  - error-message class names for COMPILE `extra_context` (GĐ4);
  - extra `count_assertions` kinds (GĐ1).
- Still open, waiting for the team review: N/P defaults, command name and `PackageId`, license, a `--no-pit` flag. Do not decide these alone.

## Secrets

- An LLM key is read only from `ARROW_SECRET_<REF>` environment variables (e.g. `ARROW_SECRET_TEAMO` for `https://api.teamorouter.com/v1`) or from the secret store.
- **Never** write a key into a file, a test, a commit, memory or chat output.
- Tests use the fake key `sk-test-SECRET-123`.
- Automated tests never call a real model; they use `FakeLlmClient`.

## Machine (Windows 11)

- .NET SDK 10.0.404 and 9.0.320 in `C:\Program Files\dotnet`.
- JDKs 8, 11, 17, 21 in `D:\Ky9\DoAn\ARROW\Java-version\runtime\jdk-<n>`. User environment variables `JAVA_HOME_<n>_X64` point to them (the CI convention). Plain `java` on PATH is Oracle JDK 17.
- Maven 3.9.16; Gradle 8.14.3 in `%LOCALAPPDATA%\Programs\gradle\gradle-8.14.3\bin` (on the user PATH); git; `gh`, logged in as `Duc-Chinh1907`.
- Validation repos for GĐ7 come from `D:\Ky9\DoAn\ARROW\preflight_manifest\*\strict_eligible_manifest.jsonl`.
- **Shell gotchas:**
  - The console is cp1252: set `PYTHONIOENCODING=utf-8` before printing Vietnamese from Python.
  - In GNU sed, `\|` means alternation; use the Edit tool for markdown tables.
  - In PowerShell 5.1, `Invoke-WebRequest` is slow and returns bytes for some text files; prefer `curl.exe`.

## Status

- Docs: done and reviewed (12/11/2026). The team review of the docs is in progress and does not block GĐ0.
- Plan: about 11.5–12 weeks, one phase after another; M1 demo after about 6.5 weeks (PLAN §7).
- GĐ0 step 1 done (12/11/2026): `docs/baseline-arrow-framework.md`. All 211 old test cases pass on net8.0 and net10.0; 117 of 164 methods are to be rewritten as parity tests.
- GĐ0 step 2 done (12/11/2026): solution scaffold pushed to `main`. Build with `dotnet build ARROW.slnx`, test with `dotnet test --solution ARROW.slnx` (Microsoft.Testing.Platform, set in `global.json`). Text files are LF everywhere (`.gitattributes`). In PowerShell 5.1, `Get-Content`/`Set-Content` corrupt UTF-8 files (they add a BOM and garble `§`), so edit files with the Edit tool or bash.
- `main` is protected (13/11/2026, Chính's choice): changes only through PRs, both `build-test` checks must pass, no approval needed (he codes alone), no admin bypass, no force-push. Work on `gd<n>/<task>` branches; merge with `gh pr merge <n> --merge --delete-branch` after CI is green.
- GĐ0 step 3 done (12/11/2026), branch `gd0/copy-assets`: 7 product prompts in `src/Arrow.Business/Prompts/`, `tools/tools.lock.json` (12 entries, no EvoSuite), 5 fixtures in `tests/fixtures/`. The sha256 values are in the commit messages, for comparison when the team changes a source.
- GĐ0 step 4 done (12/11/2026), PR `gd0/common`: `Arrow.Common` has `ArrowJson` (P00 §5), the shared enums and records (`ProjectTarget`, `CutInfo`, `TargetResult`, `RunInfo`, `ArrowEvent`), `ErrorCodes` (57 codes), `ErrorCatalog` (embedded vi/en JSON, `{name}` placeholders) and `ArrowException`. Its tests live in `Arrow.Business.Tests/Common`. Embedded resources whose names contain `.vi.`/`.en.` need `WithCulture="false"`.
- GĐ0 step 5 done (13/11/2026), PR `gd0/dataaccess`: `ArrowHome`, `UserConfigStore` (5 layers with origins), `SecretStore` (DPAPI / mode 600, env first), `RunStore`, `Redactor`, `AtomicFile`, `JsonlFile`, `ProcessRunner` (kills the tree, P00 §5 log, 2 MB + 2 MB above 5 MB); config records and `ArrowDefaults` (embedded `defaults.json`) in Common; `ConfigValidator` in Business. 107 tests pass.
  - `cmd.exe` prints in the console code page, not UTF-8: keep non-ASCII text out of `.cmd` test scripts.
- GĐ0 step 6 done (13/11/2026), PR `gd0/cli`: `CliApp` (System.CommandLine 2.0.14; root is a plain `Command` named `CliApp.CommandName = "arrow"` so help does not show the assembly name, which stays `Arrow.Cli` until the team decides the name), `Renderer` (rich/plain/json), `ExitCodes`, `config` and `secrets` commands, `ConfigUseCase`/`SecretsUseCase` in Business, `FileAppLog` (P09 §7). 139 tests pass, including E2E on the real executable.
  - Tests run the CLI in-process through `CliRunner` (`--home` temp folder, fake streams, empty environment); use `RunWithInputAsync` to pass stdin.
- GĐ0 step 7 done (13/11/2026), PR `gd0/fixtures`: `maven-no-tests` (Java 8, JUnit 5, no test), `maven-testng` (Java 11, TestNG 7.10), `maven-wrapper-junit5` (Java 11, `mvnw` → Maven 3.9.9), `maven-existing-arrowtest` (Java 8, JUnit 4, `_ArrowTest` exists → `_Arrow2Test`), each with README and `.arrow-sample/`; `NewFixtureTests` builds them on temp copies. 144 tests pass.
  - The 5 copied fixtures still lack `.arrow-sample/` and the README fields of P11 §2; add them in GĐ5, when block 4 needs them.
  - **Windows:** start Maven by its absolute path (`…\bin\mvn.cmd`). Starting `"mvn.cmd"` by name from .NET exits 1 with no output. Block 1 (GĐ2) must resolve build tools to absolute paths.
  - **TestNG + Surefire:** several classes are reported together in `TEST-TestSuite.xml`, and reports of an earlier run stay in `surefire-reports/` until `clean`; `junitreports/` holds duplicates. The report reader of GĐ2 must not count them twice.
- GĐ0 step 8 done (13/11/2026), PR `gd0/ci`: `.github/workflows/ci.yml` (P11 §5.1) on `windows-latest` and `ubuntu-latest`, green with 0 skipped tests. Test steps filter by xUnit trait: `--filter-not-trait "Category=Integration" --filter-not-trait "Category=E2E"`, `--filter-trait "Category=Integration"`, `--filter-trait "Category=E2E"` (Ubuntu only), each with `--ignore-exit-code 8`. Mark new integration tests `[Trait("Category", "Integration")]` or `"E2E"`, otherwise they run as unit tests.
  - Step still to add: `ARROW_HOME/config.json` pointing to the JDKs (GĐ2). `mvn verify` of java-helper was added in GĐ1.
  - Write paths in tests with `Path.Combine`/temp folders: `D:/x` is a relative path on Linux.
- **GĐ0 is complete** (13/11/2026), branch protection included.
- GĐ1 done (13/11/2026), PR `gd1/java-helper`: java-helper copied verbatim (fingerprint `b687e0f2d605db9d`), then 1.1.0 with `list_classes` and the R03 §3.6 assertion kinds (25 JUnit tests); `JavaHelperClient`, `JavaHelperPool`, `JavaHelperJar` in `Arrow.DataAccess/JavaHelper`; CI runs `mvn verify` of java-helper before `dotnet build`. 160 .NET tests pass.
  - **Build order:** `mvn -q -f tools/java-helper/pom.xml package` (or `verify`) first, then `dotnet build`; the jar is embedded only if it exists. After changing java-helper, rebuild both, and keep `JavaHelperJar.Version` equal to `Main.VERSION`.
  - java-helper tests need a JDK ≥ 17 in `JAVA_HOME_17_X64` or `JAVA_HOME_21_X64`; without it or without the jar they are skipped.
- GĐ2 (block 1, P01) in 4 PRs (13/11/2026): #8 readers and rules (`Arrow.DataAccess/Build`, `Arrow.Business/Analysis`), #9 sources and tools (`Source/`, `Tools/`, `BuildExecutor`), #10 `ProjectAnalyzer`/`ModuleAnalyzer`/`AnalysisCache`, and the PR with `arrow inspect` and `arrow doctor` (`IInspectUseCase`, `IDoctorUseCase`, `IConfirmation`).
  - Block 1 keeps the user's project untouched: every integration test compares the fixture fingerprint before and after.
  - Tests of the commands use fake use cases: `CliRunner.Services` replaces them after `CompositionRoot` registers the real ones.
  - Real-repo trials (`arrow inspect … --repo <url> --ref <full sha>`; the ref must be a full SHA, a short one is refused): `gephi/graphstore` (Maven, TestNG, 1,841 tests, block 1 in 76 s), `intendia-oss/qualifier` (multi-module Maven, JDK 8, 12 s), `tolpp/memguard` (Gradle with a 2017 wrapper; see the PR).
  - Java allows a top-level class in a file with another name, so the source root comes from the package, not from the class name.
  - Run `arrow` from the build output: `dotnet src/Arrow.Cli/bin/Debug/net10.0/Arrow.Cli.dll <command> --home <short ascii folder>`.
- GĐ3 (LLM and block 2) in 3 PRs (13/11/2026): #12 the LLM layer (`Arrow.DataAccess/Llm`, `Arrow.Business/Llm`), #13 block 2 (`Arrow.Business/Generation`: `PromptTemplateStore`, `PromptBuilder`, `CodeExtractor`, `TestGenerator`), and the PR with `arrow setup`, `arrow models` and the model lines of `doctor`.
  - The chain for a run is `ReplayingLlmClient` → `LlmGateway` → provider client, built by `ILlmClientFactory.Create`; `CreateProvider` gives the bare client for `models test` and `doctor` (one send, no 20/60/120 s retries).
  - Tests of HTTP use `FakeHttp` and `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`), so no test waits for a retry.
  - `ARROW_FAKE_LLM=<rules file>` makes the CLI use `FakeLlmClient` (P11 §3.1) and print `[!] FAKE LLM` first.
  - The CLI wizard asks through `IWizardPrompter` (`SpectreWizardPrompter`); tests give a scripted one and `CliRunner.Interactive = true`.
  - **Not smoke-tested with a real model yet:** `ARROW_SECRET_TEAMO` was not set on the machine. Run `arrow setup`, then `arrow models test` once.
- GĐ4 (block 3 and milestone M1, P04) in 4 PRs: `gd4/classify`, `gd4/policy`, `gd4/verifier`, `gd4/gen`. Use the larger model for this phase.
  - PR `gd4/classify` (13/11/2026): verification models in `Arrow.Common/Verification` (`VerifyResult`: a null `State` is INFRA, written `"INFRA"`), and in `Arrow.Business/Repair` `JavacErrorParser` (maven-compiler-plugin 2.x, lint warnings under `-Werror`), `TestResultClassifier`, `RegressionChecker`, `SignatureBuilder`, `BuildErrorExtractor`, `FailureDiagnoser`, `CheckpointComparer`, `LineDiff`.
  - Where the P-spec changed the old behaviour: the project frame uses `project_structure.packages` and the CUT package, falling back to the first frame; error blocks are ≤ 60 lines and diffs ≤ 15 lines *including* the "more" line; first errors ≤ 200 characters including `…`; the REGRESSION block has a separate part for case (c); `initializationError` is not a failing method.
  - PR `gd4/policy` (13/11/2026): `AdaptiveRepairPolicy`, `RoundDecision` (`decision.json`), `RepairCounters`, `ChainVersion`, `RoundOutcome`, `IRepairPolicy` in `Arrow.Business/Repair`. The runner calls `DuplicateOf` before verifying, never applies an INFRA round, and fills `extra_context` and the `llm_*`/`build_seconds` fields of the decision. Stagnation is checked before the budget (P04 §6.3 pseudo-code).
  - PR `gd4/verifier` (14/11/2026): `Verifier` (`SuiteFiles`, `TaskContext`, `VerifyOptions`, `IVerifier`) and `ExtraContextBuilder` in `Arrow.Business/Repair`.
    - Each verification copies the snapshot to `work/<run>/v-<8 hex>`: steps 1–2 share it, step 5 gets another. Infrastructure retries also use new copies. The verifier makes paths in failures relative, since `FailureDiagnoser` does not see the workspace.
    - Logs go to `<OutputDir>/logs/<step>[-try<k>].log` and reports to `<OutputDir>/reports/<step>/`. The runner (PR 4) writes `verify.json`. c0 needs an output folder (e.g. `block3/c0/`) that P09 §5.2 does not list.
    - `extra_context` warnings come from java-helper failures; they never stop the round.
    - Unit tests use a fake `IBuildExecutor` and `FakeHelper` (`tests/Arrow.Business.Tests/Repair/VerifierTests.cs`). `VerifierIntegrationTests` run block 1 and then the verifier on the fixtures (about 4 min, including a 3 × 20 s timeout).
    - `ProcessRunnerTests` once asserted that stderr comes after stdout. Two pipes have no fixed order, so that assertion was wrong. `ProcessRunner` now waits for the null line of each stream.
  - PR `gd4/gen` (14/11/2026), **milestone M1**:
    - `RepairRunner` (`IRepairRunner`, `RepairOptions`, `FinalTestSuite` = `final.json`).
    - `GenerateTestsUseCase` (minimal: one class, blocks 1–3, `result.json`, `apply.json`, history line), `CutPicker` (shared with inspect), `RunProgress`.
    - `ProjectFileWriter` in `Arrow.DataAccess/Apply`, and `arrow gen`.
    - `IAnalyzerHost` also gives `JavaHelpers` and `Verifier`, so one java-helper pool serves blocks 1–3.
    - Scenarios: `tests/scenarios/valid-at-c0` and `o8-o3-o4-o7-o1`, for `maven-verify-junit4`/`PriceCalculator`. `GenExecutableTests` (E2E) run the executable with `ARROW_FAKE_LLM`.
    - Manual check: `arrow config set llm.providers.local '{"kind":"openai-compatible","base_url":"http://localhost:9/v1","secret_ref":null}'`, `arrow config set llm.models.fake '{"provider":"local","model_id":"fake-model"}'`, `arrow models default fake`, then `ARROW_FAKE_LLM=<rules.json> arrow gen PriceCalculator --project <copy> --no-metrics --yes`.
    - Real model (14/11/2026): Chính ran `arrow setup` (model `gpt6Luna` = `gpt-6-luna` on TeamoRouter, key `teamo` in the secret store); `arrow models test` OK; `arrow gen PriceCalculator --no-metrics --yes` on a copy of `maven-verify-junit4` gave c0 ASSERTION 5/6, then VALID in round 1, 46 s, 3.7k tokens; `mvn test` of the copy passes. **GĐ4 is complete.**
- GĐ5 (block 4, P05) in 3 PRs: `gd5/tools`, `gd5/parsers`, `gd5/quality`.
  - PR `gd5/tools` (14/11/2026): `ToolStore`/`ToolLock` in `Arrow.DataAccess/Tools` (`tools/tools.lock.json` embedded as `Arrow.DataAccess.Tools.tools.lock.json`; HTTPS from Maven Central or `ARROW_MAVEN_MIRROR`; sha256 check, `.part` then rename, `tools/tools.installed.json`; `EnsureMavenArtifactAsync` checks the `.sha1` of Maven Central), `ToolsUseCase`, `arrow tools list|install`. A real `arrow tools install` downloads the 12 tools in about 5 s and every sha256 matches.
  - PR `gd5/parsers` (14/11/2026): `Arrow.Common/Quality` (`CoverageResult`, `MutationResult` with `survivors_sample`, `SmellResult`, `ToolOutcome`, `QualityResult`), and in `Arrow.Business/Quality` `ResultParsers` (JaCoCo CSV, `mutations.xml`, tsDetect CSV), `PitSetup` (JDK, classpath, launch classpath, arguments) and `JacocoSetup` (agent option).
  - PR `gd5/quality` (14/11/2026):
    - `SuitePreparer`, `JacocoTool`, `PitTool`, `TsDetectTool`, `JavaRunner`, `QualityService` (`QualityTools.cs`), wired into `arrow gen` when the final suite is VALID and measuring is on.
    - `IBuildExecutor.RunAsync` takes `extraJavaToolOptions` (the JaCoCo agent). `IAnalyzerHost` also gives `Builds` and `Workspaces`.
    - Copies are `work/<run>/q-<tool>-<6 hex>` and `ts-<6 hex>`; files go to `targets/<cut>/block4/` (`jacoco.json`, `pit.json`, `tsdetect.json`, `outcome.json`, the raw CSV/XML and `logs/`).
    - `.arrow-sample/` added to `maven-verify-junit4`, `maven-multi-junit5`, `gradle-junit5`; READMEs give the measured values. The `maven-testng` README said 100% line and branch; the measurement gives 90% and 86%, since its sample never adds a positive amount.
    - `QualityIntegrationTests` measure the 4 fixtures (about 3 min; tools downloaded once into `%TEMP%/arrow-quality-tools`).
- **GĐ5 is complete** when PR `gd5/quality` is merged.
- GĐ5 is complete (PRs #19–#21).
- GĐ6 (full `arrow gen`, interactive session; P06, P07, P08) in PRs: `gd6/multi`, `gd6/apply`, `gd6/resume`, `gd6/show`, `gd6/display`, `gd6/repl`.
  - PR `gd6/multi` (14/11/2026):
    - `GenerateTestsUseCase` takes a `GenerateRequest` (several classes, `--package`, `--jobs`). It runs the cost estimate and question of P07 §4, then runs targets in parallel with `SemaphoreSlim(jobs)`, sharing one model client and block 1 per module (`AnalysisCache`).
    - A P03 stop starts no new target, and the exit codes follow P07 §9.
    - The per-target pipeline is `TargetPipeline` (`PipelineShared`, `TargetRun`); the write step is `ProjectApplier`, run after all targets. `TestWritePolicy` replaces it in `gd6/apply`.
    - `ToolStore` retries HTTP 429/5xx and network errors (2/5/10 s or `Retry-After` ≤ 30 s): Maven Central answered 429 after many test downloads. `GenExecutableTests` copy tools from `%TEMP%/arrow-quality-tools/tools`.
    - New scenario `tests/scenarios/two-classes`.
  - PR `gd6/apply` (14/11/2026):
    - `TestWritePolicy` and `ApplyStep` in `Arrow.Business/Apply` implement P06 in full, replacing `ProjectApplier`.
    - Checks: §3 (inside `test_root`, CUT unchanged, name still free).
    - Naming (§4.2): when the name appeared during the run, the policy picks the next `_Arrow<k>Test`, renames with java-helper `rename_class`, and verifies once more on a fresh copy (`targets/<cut>/apply/reverify`). If the file appears at the moment of writing, it renames once more.
    - Questions: one for one class, or one multi-select for several (`IConfirmation.ChooseManyAsync`, Spectre `MultiSelectionPrompt`). A changed CUT is never written with `--yes`.
    - `TargetRun` keeps the `TaskContext`, which the second verification needs.
  - PR `gd6/resume` (14/11/2026), P07 §5:
    - `arrow gen --resume <run>`, or an unfinished `gen` run of the same project from the last 7 days that the user agrees to resume (`--yes` agrees; no keyboard starts a new run).
    - The settings of the old run win, with a warning when the options differ.
    - The project must be unchanged: a local project must have the same `source_tree_hash` once the files this run wrote are ignored (`LocalProjectTree.SourceTreeHashWithout`, paths from `apply.json`); a repository must have the same commit. Otherwise `GEN_RESUME_MISMATCH`.
    - DONE targets other than BLOCKED/INFRA are kept; a kept target without `apply.json` still goes to the write step, rebuilt from `final.json` and `final.java`. Other targets are deleted and run again, and model calls replay from `llm_cache`. Block 1 runs again too, since `work/<run>` is deleted at the end of a run.
    - `IRunStore.Recent(limit)` lists runs newest first. Scenario `stop-at-second` gives a P03 stop at the second class.
  - PR `gd6/show` (14/11/2026):
    - `ShowRunUseCase` (`HistoryView`, `RunView`, `TargetView`, `RoundView`) and `ShowCommand`: `arrow history [--limit]`, `arrow show <run> [<class>]`, with `--json`.
    - A run that ends by Ctrl+C or an error also gets its `history.jsonl` line (P07 §8).
- `docs.zip` in the root is a copy Chính made for the team. Keep it out of git.
