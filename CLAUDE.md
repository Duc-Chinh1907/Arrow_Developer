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
- Branch protection on `main` is not set yet; turn it on once `ci.yml` exists (GĐ0 step 8). From now on, work on `gd0/<task>` branches and merge through PRs.
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
  - Steps still to add: `mvn verify` of `tools/java-helper` (GĐ1), `ARROW_HOME/config.json` pointing to the JDKs (GĐ2).
  - Write paths in tests with `Path.Combine`/temp folders: `D:/x` is a relative path on Linux.
- **GĐ0 is complete** except branch protection on `main` (required checks `build-test (windows-latest)` and `build-test (ubuntu-latest)`), which needs Chính's go-ahead because it changes the GitHub repo settings.
- **Next:** GĐ1, java-helper (P10): copy `tools/java-helper` verbatim in its own commit, `mvn verify`, add `list_classes`, extend `count_assertions` (R03 §3.6), new `JavaHelperClient` in DataAccess, GĐ1 parity tests (`docs/baseline-arrow-framework.md` §5.2).
- `docs.zip` in the root is a copy Chính made for the team. Keep it out of git.
