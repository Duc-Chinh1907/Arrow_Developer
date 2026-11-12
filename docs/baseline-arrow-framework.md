# Baseline của `ARROW_Framework` (GĐ0 bước 1)

**Mục đích:** ghi lại trạng thái test của code nền trước khi viết mới, và chốt danh sách test cũ sẽ được viết lại làm **test đối chiếu** cho từng giai đoạn (PLAN §2.1, §3).

**Ngày đo:** 12/11/2026. **Người đo:** Claude Code, theo yêu cầu của Chính.

## 1. Bản được đo

| Mục | Giá trị |
|---|---|
| Nguồn | `D:\Ky9\DoAn\ReportDoAn\ARROW_Framework` (chỉ đọc, chép sang thư mục tạm rồi mới build) |
| Phiên bản | Thư mục không phải git repo, nên không có commit. File `.cs` sửa gần nhất: 15/10/2026 |
| Dấu vân tay nội dung | sha256 của danh sách sha256 từng file (bỏ `bin/`, `obj/`, `target/`), 16 ký tự đầu: `src/` 100 file `a1188c0b0263f2d1` · `tests/` 56 file `f129e3002d34b8bb` · `prompts/` 12 file `605fe16790cb0556` · `tools/java-helper/` 16 file `835600c28ae3dd0b` · `tools/tools.lock.json` `ba8cd17e4a6cf283` |
| Bản trùng | `D:\Ky9\DoAn\ExperimentARROW\ARROW_Framework` có `src/` và `tests/` giống hệt (cùng dấu vân tay) |
| Cỡ | 14.913 dòng C# trong `src/`, 4.636 dòng trong `tests/` |
| Không chép, không đọc | `litellm.config.local.yaml`, `machine.local.json` (có khoá); `runs/`, `logs/`, `bin/`, `obj/` |

## 2. Môi trường dựng lại

Test cũ coi **thư mục cha** của `ARROW_Framework` là `ArrowRoot` và tìm trong đó `Java-version/runtime/jdk-<n>`, `tools/` (JaCoCo, PIT, tsDetect) và `evaluation_manifest/`. Môi trường gốc là `D:\Ky9\DoAn\ExperimentARROW\`. Bản đo dựng lại cấu trúc đó trong một thư mục tạm:

```text
<tạm>/ARROW/
  ARROW_Framework/       bản chép
  Java-version/          junction → D:\Ky9\DoAn\ARROW\Java-version (JDK 8, 11, 17, 21)
  tools/                 chép từ ExperimentARROW/tools (jacoco-0.8.12, pit-1.17.4, tsdetect-2.2, junit-platform, evosuite-1.2.0)
  evaluation_manifest/   chép từ ExperimentARROW/evaluation_manifest
```

- .NET SDK 10.0.404 (`global.json` của code cũ ghim `8.0.400` với `rollForward: latestMajor`, nên SDK 10 được chọn); runtime 8.0.33 và 10.0.15.
- Maven 3.9.16, Gradle 8.14.3, git trong `PATH`; `~/.m2` đã có sẵn dependency của fixture.
- Không test nào gọi model thật: mọi test dùng `FakeLlmClient` hoặc HTTP giả.

## 3. Kết quả

Build: **0 cảnh báo, 0 lỗi** (`TreatWarningsAsErrors` bật).

| Project test | Method | Ca chạy | `net8.0` (gốc) | `net10.0` (đổi `TargetFramework`) | Thời gian |
|---|---|---|---|---|---|
| `Arrow.DataAccess.Tests` | 23 | 23 | 23 pass | 23 pass | < 1 giây |
| `Arrow.Service.Tests` | 95 | 140 | 140 pass | 140 pass | 1 giây |
| `Arrow.IntegrationTests` | 46 | 48 | 48 pass | 48 pass | 4 phút 40 giây (`net8.0`), 4 phút 5 giây (`net10.0`) |
| **Tổng** | **164** | **211** | **211 pass, 0 hỏng, 0 bỏ qua** | **211 pass** | |

- **Không có test hỏng**, nên mọi test đối chiếu đều có kết quả mong đợi từ code cũ. Rủi ro "Test cũ không chạy được trên .NET 10" (PLAN §8) không xảy ra: code cũ đổi sang `net10.0` vẫn build sạch và pass hết.
- `AssessmentIntegrationTests.TsDetectWhenInstalled` **chạy thật** (2,3 giây), vì `tools/tsdetect-2.2` có mặt. Nếu thiếu công cụ, test này `return` sớm và vẫn báo pass (xem Mục 6).

## 4. Integration test cần gì

Mọi test dưới đây cần git (fixture được `git init` trong bản sao) và mạng tới Maven Central ở lần chạy đầu.

| Class test | Fixture | Build | JDK dùng thực tế | Công cụ khác | Thời gian |
|---|---|---|---|---|---|
| `Block1IntegrationTests` (9) | `maven-subdir-junit4`, `maven-multi-junit5`, `maven-small-stack`, `gradle-junit5` | Maven, Gradle | 8 (`subdir`, `small-stack`), 11 (`multi`, khai báo `release 11`), 17 (`gradle`, khai báo `VERSION_17`) | `evaluation_manifest/` (chỉ test screening) | 0–63 giây/test |
| `VerifierIntegrationTests` (9) | `maven-verify-junit4` | Maven | 8, dự phòng 17 | `evosuite-1.2.0` (1 test EvoSuite) | 0–27 giây/test |
| `GradleVerifierIntegrationTests` (1) | `gradle-junit5` | Gradle (`--no-daemon`) | 17 | | **3 phút 32 giây**, chậm nhất |
| `RepairChainIntegrationTests` (2) | `maven-verify-junit4` | Maven | 8 | | 19–27 giây |
| `AssessmentIntegrationTests` (2) | `maven-verify-junit4` | Maven | 8 cho build, 17 cho công cụ | JaCoCo 0.8.12, PIT 1.17.4, tsDetect 2.2, `junit-platform` | 2–31 giây |
| `PipelineIntegrationTests` (2) | `maven-verify-junit4` | Maven | 8 | toàn bộ `tools/` | 1,5–2,8 phút |
| `CliIntegrationTests` (2) | `maven-verify-junit4` | Maven | 8 | | 4–67 giây |
| `JavaHelperTests` (7), `TestGeneratorTests` (4), `JavaHelperBuildTests` (1) | | Maven (build jar java-helper) | 17 | | < 23 giây |
| `ProcessRunnerTests` (3), `EndToEndC1Tests` (4) | | | 17 (`ProcessRunnerTests` chạy `java`) | | < 2 giây |

Unit test (`Arrow.DataAccess.Tests`, `Arrow.Service.Tests`) không cần JDK hay Maven, **trừ**:
- `Block1Tests` đọc `evaluation_manifest/` thật ở 2 test (`BuildRootIsSubdirectoryForLibsodium`, `FailedReactorFallsBackToStandaloneModule`), `ManifestPromptTests` cũng vậy ở 2 test. Cả 4 test đều bị bỏ (Mục 5.8).
- `Block1Tests.RemovesOnlyCorruptJarsInsideBuildCaches` ghi rồi xoá một file trong `~/.m2/repository` thật.

## 5. Danh sách test đối chiếu theo giai đoạn

**Ký hiệu:**
- **ĐC**: viết lại theo interface mới, giữ nguyên đầu vào và kết quả mong đợi.
- **ĐC\***: viết lại, nhưng **một phần đầu vào hoặc kết quả đổi theo P-spec**. Ghi chỗ khác vào mô tả PR (PLAN §2.1).
- **Bỏ**: chỉ dành cho thực nghiệm (P00 §7) hoặc thuộc phần không dùng lại. Lý do ghi ở Mục 5.8.

Đường dẫn file tính từ `ARROW_Framework/tests/`. Số trong ngoặc vuông là số ca của `[Theory]`.

**Tổng:** 164 method, trong đó **117 viết lại** (ĐC hoặc ĐC\*) và **47 bỏ**.

| Giai đoạn | Số test đối chiếu |
|---|---|
| GĐ0 | 17 |
| GĐ1 | 7 |
| GĐ2 | 25 |
| GĐ3 | 27 |
| GĐ4 | 33 |
| GĐ5 | 6 |
| GĐ6 | 2 |

### 5.1 GĐ0: nền móng (17)

| Test cũ | Loại | Thành phần mới | Ghi chú |
|---|---|---|---|
| `DataAccess.Tests/FileHelpersTests.AtomicWriteOverwritesAndLeavesNoTempFiles` | ĐC | `AtomicFile` | |
| `…FileHelpersTests.ConcurrentAtomicWritesToSameFileDoNotFail` | ĐC | `AtomicFile` | |
| `…FileHelpersTests.JsonlAppendFromManyThreadsKeepsEveryLine` | ĐC | `RunStore` (`events.jsonl`, `history.jsonl`) | |
| `…FileHelpersTests.JsonlIgnoresHalfWrittenLastLine` | ĐC | `RunStore` | |
| `…FileHelpersTests.JsonUsesSnakeCaseAndEnumNamesFromSpec` | ĐC\* | quy ước JSON (P00 §5) | Tên enum theo P-spec |
| `…FileHelpersTests.DictionaryKeysAreNotRenamed` | ĐC | quy ước JSON | |
| `…RunRepositoryTests.EventsAreRedactedBeforeWriting` | ĐC | `Redactor` + `RunStore` | Dùng khoá thử `sk-test-SECRET-123` |
| `…RunRepositoryTests.RedactorPatterns` | ĐC | `Redactor` | |
| `…RunRepositoryTests.TaskResultsSaveLoadDeleteAndRejectEscapingPaths` | ĐC\* | `RunStore` (P09 §5) | Giữ phần chặn đường dẫn thoát ra ngoài; cấu trúc thư mục theo P09 |
| `Service.Tests/ArchitectureTests` (3 test) | ĐC\* | `ArchitectureTests` | Viết lại cho `Cli → Business → DataAccess`, `Common`; Cli chỉ tham chiếu DataAccess trong `CompositionRoot` |
| `IntegrationTests/ProcessRunnerTests.CapturesOutputExitCodeAndEnvironment` | ĐC | `ProcessRunner` | |
| `…ProcessRunnerTests.TimeoutKillsProcess` | ĐC | `ProcessRunner` | Giết cả cây tiến trình |
| `…ProcessRunnerTests.CancellationKillsProcessAndThrows` | ĐC | `ProcessRunner` | |
| `…EndToEndC1Tests.UnknownCommandFailsWithCode2` | ĐC\* | `Arrow.Cli` | Mã thoát 2, `CLI_ARGS` (P08 §8); câu báo lỗi theo P08 |
| `…EndToEndC1Tests.ServicesWriteToolLog` | ĐC\* | log (P00 §5) | Log ghi dưới `ARROW_HOME`, không dưới thư mục framework |

### 5.2 GĐ1: java-helper (7)

Các test JUnit của java-helper được chép nguyên cùng java-helper, không tính ở đây.

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `IntegrationTests/JavaHelperTests.PingParseAndNormalizeNames` | ĐC | |
| `…JavaHelperTests.HashCountsTestsAndLines` | ĐC | Thêm ca cho các loại assertion mới ở GĐ1 bước 4 |
| `…JavaHelperTests.SpliceKeepsOtherMethodsAndReportsChanges` | ĐC | |
| `…JavaHelperTests.ClassApiFromSourceAndFromJarOnClasspath` | ĐC | |
| `…JavaHelperTests.MethodSourcesAndCalledMethods` | ĐC | |
| `…JavaHelperTests.ParallelCallsAreServedOneByOne` | ĐC\* | Bản mới có pool theo `--jobs` (P10 §5): trong một tiến trình vẫn tuần tự |
| `…JavaHelperTests.RestartsAfterProcessIsKilled` | ĐC | Tự khởi động lại một lần |

### 5.3 GĐ2: khối 1 (25)

Unit, `Service.Tests/Block1Tests`:

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `ParsesManifestJavaMajor` [6] | ĐC\* | Đầu vào là chuỗi `java -version`; dùng để dò major của JDK trên máy (`doctor`, `setup`), không còn đọc từ manifest |
| `ParsesDeclaredLevels` [7] | ĐC | `JavaVersionResolver` (P01 §4.3) |
| `GradleProjectFromTask` | ĐC\* | Giữ `GradleProbe.Task`; bỏ các ca đọc lệnh từ manifest (`ToolOf`, `GradleProject`) |
| `NoSuccessfulAttemptGivesNull` | ĐC\* | Chỉ giữ các ca `RelativeTo` |
| `SelectsSmallestJdkAtLeastDeclared` | ĐC\* | **Kết quả đổi theo P01 §4.3:** không còn JDK của manifest. Không có mức khai báo thì JDK 17, rồi JDK lớn nhất (`default`); thứ tự dự phòng là các JDK còn lại tăng dần. Giữ các ca: mức < 8 coi là 8, mức 19 → 21, `fallback:17,8` |
| `ReadsEffectivePomOfTargetModule` | ĐC | |
| `ParsesDependencyListLines` | ĐC | |
| `ParsesGradleProbeOutput` | ĐC\* | Bổ sung dòng `include`/`exclude` của task `test` (P01 §4.7) |
| `DetectsTestFrameworks` | ĐC\* | Bỏ ca lấy framework từ manifest; thay bằng `name = null`, `missing_reason` (P01 §4.5) |
| `ComputesPaths` | ĐC\* | **Tên khi trùng đổi:** `B_ArrowTest2` → `B_Arrow2Test` (P01 §4.7) |
| `ClassifiesBaselineRuns` | ĐC | |
| `RendersMetadataPromptLikeSpecExample` | ĐC\* | So với ví dụ ở P01 §5.2 |
| `ClassifiesBuildFailures` | ĐC | Gồm phân loại lỗi `git fetch` cho repo GitHub |
| `BuildsMavenCommandsWithSelector` | ĐC | |
| `ReadsSurefireXml` | ĐC | Lớp đọc báo cáo dùng chung với khối 3 |
| `GradleOwnerIsDeepestProjectContainingCut` | ĐC | |
| `RemovesOnlyCorruptJarsInsideBuildCaches` | ĐC\* | Không ghi vào `~/.m2` thật: truyền thư mục cache vào làm tham số |

Integration, `IntegrationTests/Block1IntegrationTests`. Code cũ nhận một `TaskRecord` có sẵn `build_attempts`; bản mới tự dò từ thư mục (P01 §4.1), nên **đầu vào đổi thành đường dẫn fixture và tên class, kết quả M giữ nguyên**:

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `SubdirectoryBuildRootJUnit4` | ĐC\* | |
| `ReactorModuleWithSiblingDependencyJUnit5` | ĐC\* | |
| `ModuleOutsideReactorUsesFallbackAndScreensIgnoredTests` | ĐC\* | Giữ phần dự phòng standalone; bỏ phần screening |
| `DetectsBuildWhenManifestHasNoAttempts` | ĐC | Đúng luồng của sản phẩm |
| `GradleProjectJUnit5` | ĐC\* | |
| `UnusableTasksAreExcludedWithCodes` | ĐC\* | Giữ `B1_CUT_MISSING`, `B1_MODULE_MISMATCH`; bỏ `B1_MANIFEST_INVALID`, `B1_COMMIT_MISSING`, `NO_PAIRED_TEST` |
| `SwitchesJdkWhenTestJvmCannotStart` | ĐC | |
| `RecognizesTestJvmStartFailures` | ĐC | Danh sách dấu hiệu ở P01 §4.3 |

### 5.4 GĐ3: LLM và khối 2 (27)

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `DataAccess.Tests/FileHelpersTests.Sha256OfTextFileIgnoresLineEndings` | ĐC | `prompts_sha256` (P02 §3.1) |
| `…ManifestPromptTests.LoadsAllRealPromptTemplates` | ĐC\* | Chỉ các template sản phẩm: `zero_shot_project_aware`, `repair/*` |
| `…ManifestPromptTests.TemplateShaIgnoresLineEndingsAndFrontMatterIsRequired` | ĐC | `PromptTemplateStore` |
| `…RunRepositoryTests.LlmCacheStoresOriginalUsage` | ĐC | `ReplayingLlmClient` |
| `Service.Tests/Block2Tests.RepairPromptsByLevelAlwaysCarryFullMetadata` | ĐC | |
| `…Block2Tests.FailedAttemptsShowDetailsOnlyForLastThree` | ĐC | P02 §3.4 |
| `…Block2Tests.FailureSummaryComesFromDataFile` | ĐC | |
| `…Block2Tests.ExtractsPlainCodeWithoutChanges` | ĐC | `CodeExtractor` |
| `…Block2Tests.RemovesFenceAndProse` | ĐC | |
| `…Block2Tests.PicksFirstFenceWithClassAndHandlesMissingCode` | ĐC | |
| `…Block2Tests.RemovesDuplicateImports` | ĐC | |
| `…Block2Tests.CacheKeyIgnoresParamOrderButSeparatesCallIds` | ĐC\* | `call_id` = `gen/<cut>`, `repair/<cut>/round_<k>` (P03 §5) |
| `…Block2Tests.ReplayKeepsOriginalTimeAndCost` | ĐC | |
| `…Block2Tests.GatewayComputesCostChecksModelAndLimitsCalls` | ĐC\* | Giới hạn bằng ngân sách `--max-tokens`, không còn `B2_CALL_LIMIT` |
| `…Block2Tests.ModelPrefixMatchesProviderPaths` | ĐC | |
| `…Block2Tests.GatewayWaitsWhenRateLimitIsReached` | ĐC | |
| `…Block2Tests.LiteLlmSendsParamsVerbatimAndParsesUsage` | ĐC\* | Cho `OpenAiCompatibleClient` (cùng định dạng OpenAI) |
| `…Block2Tests.LiteLlmRetriesTransientErrors` | ĐC\* | Số lượt gọi lại theo P03 §4 |
| `…Block2Tests.LiteLlmStopsOnConfigurationErrors` | ĐC\* | Mã lỗi theo P03 §11 |
| `…PromptTests.RenderFillsEveryPlaceholderOnce` | ĐC | |
| `…PromptTests.RenderRejectsMissingExtraOrUndeclaredValues` | ĐC | |
| `…PromptTests.RealProjectAwareUserPromptRenders` | ĐC | |
| `…PromptTests.RealPromptSetIsConsistent` | ĐC\* | Chỉ bộ template sản phẩm |
| `IntegrationTests/TestGeneratorTests.GenerateNormalizesFencedAnswerAndWritesEveryFile` | ĐC\* | Tên và vị trí file theo P02 §6 |
| `…TestGeneratorTests.UnusableAnswersAreInvalidOutput` [3] | ĐC | |
| `…TestGeneratorTests.Level3SplicesOnlyFailingMethodsIntoCheckpoint` | ĐC | |
| `…TestGeneratorTests.LongCutIsShrunkBeforePrompting` | ĐC | P02 §3.3 |

### 5.5 GĐ4: khối 3 (33)

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `Service.Tests/Block3ClassificationTests.ParsesJavacErrorsOnEveryPathStyle` | ĐC | Bổ sung log maven-compiler-plugin 2.x (PLAN GĐ4) |
| `…Block3ClassificationTests.NormalizesJavacKinds` [18] | ĐC | |
| `…Block3ClassificationTests.ClassifiesDiscoveryRuntimeAssertionAndPass` | ĐC | |
| `…Block3ClassificationTests.RecognizesAssertionExceptions` [11] | ĐC | |
| `…Block3ClassificationTests.ExceptionNameFallsBackToTraceWhenTypeIsAMessage` | ĐC | |
| `…Block3ClassificationTests.FindsRegressions` | ĐC | |
| `…Block3ClassificationTests.BuildsSignaturesForEveryState` | ĐC | |
| `…Block3ClassificationTests.ExtractsBuildErrorBlocks` | ĐC | |
| `…Block3ClassificationTests.ReaderKeepsTraceAndStripsGradleParentheses` | ĐC | |
| `…FailureDiagnoserTests` (4 test) | ĐC | `ExtraContextBuilder` bổ sung class nêu trong thông điệp lỗi (PLAN GĐ4) |
| `…RepairPolicyTests.Section7TableAdaptive` | ĐC | |
| `…RepairPolicyTests.Section14_5AdaptiveChain` | ĐC | |
| `…RepairPolicyTests.Q5LevelThreeRotatesBackToTwoWithLongerAttemptList` | ĐC | |
| `…RepairPolicyTests.Q5LevelThreeNotApplicableUsesLevelTwo` | ĐC | |
| `…RepairPolicyTests.InvalidOutputCountsAsRoundAndRaisesLevel` | ĐC | |
| `…RepairPolicyTests.CheckpointComparisonFollowsStateThenPassedThenAssertions` [8] | ĐC | |
| `…RepairPolicyTests.Section15_3RegressionThenRollbackThenValid` | ĐC | |
| `…RepairPolicyTests.EarlyStopAfterPRoundsWithoutImprovement` | ĐC | `early_stop_p` = 3 |
| `…RepairPolicyTests.AttemptsCarryFirstErrorAndChangedLines` | ĐC | |
| `…RepairPolicyTests.LineDiffLimitsToFifteenLines` | ĐC | |
| `…RepairPolicyTests.SiblingModuleClassDirectoriesResolveAgainstSnapshot` | ĐC | Classpath tương đối với gốc workspace (P01 §4.4) |
| `IntegrationTests/RepairChainIntegrationTests.AdaptiveSkipsDuplicateRaisesLevelAndSucceeds` | ĐC | |
| `…VerifierIntegrationTests.ValidCandidatePassesAllFiveSteps` | ĐC | |
| `…VerifierIntegrationTests.MissingMethodIsCompile` | ĐC | |
| `…VerifierIntegrationTests.BuildFailureOutsideGeneratedFileIsUnknown` | ĐC | |
| `…VerifierIntegrationTests.ClassWithoutTestsIsDiscovery` | ĐC | |
| `…VerifierIntegrationTests.NullPointerInCutIsRuntime` | ĐC | |
| `…VerifierIntegrationTests.WrongExpectedValueIsAssertion` | ĐC | |
| `…VerifierIntegrationTests.ChangingSharedStaticStateIsRegression` | ĐC | |
| `…GradleVerifierIntegrationTests.GradleCandidateIsValidAndWrongOneIsAssertion` | ĐC | |

### 5.6 GĐ5: khối 4 (6)

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `Service.Tests/Block4Tests.JacocoTakesCutAndNestedClassesOnly` | ĐC | CSV JaCoCo |
| `…Block4Tests.PitCountsStatusesAndDetectsOutOfScopeMutants` | ĐC | `mutations.xml` |
| `…Block4Tests.TsDetectSmellsAndDensity` | ĐC | CSV tsDetect |
| `…Block4Tests.PlatformVersionFromClasspath` | ĐC | Chọn `junit-platform-launcher` cho PIT |
| `IntegrationTests/AssessmentIntegrationTests.JacocoAndPitMeasureOnlyTheCut` | ĐC | |
| `…AssessmentIntegrationTests.TsDetectWhenInstalled` | ĐC\* | Không được âm thầm pass khi thiếu công cụ (Mục 6) |

### 5.7 GĐ6: `arrow gen` đầy đủ (2)

| Test cũ | Loại | Ghi chú |
|---|---|---|
| `Service.Tests/Block2Tests.WrongModelIdThroughWholeStackPausesRun` | ĐC\* | Sai model thì run dừng, giữ được để chạy tiếp, mã thoát 2 (P07 §8) |
| `IntegrationTests/PipelineIntegrationTests.RunAllBlocksStopAndResume` | ĐC\* | Giữ tính chất: dừng rồi chạy tiếp cho cùng kết quả, lượt gọi LLM đã cache không gọi lại (P07 §5). Luồng là `GenerateTestsUseCase`, không còn pipeline theo khối |

### 5.8 Bỏ (47)

| Nhóm | Test cũ | Lý do |
|---|---|---|
| Manifest, experiment, máy | `ConfigRepositoryTests` (5), `ManifestPromptTests.ReadsRealPilotManifestFields`, `…ReadsEvaluationManifestFallbackAttempts`, `Block1Tests.BuildRootIsSubdirectoryForLibsodium`, `…FailedReactorFallsBackToStandaloneModule`, `…ReactorPlanAndWindowsPaths`, `…ParsesTaskSelectionAndDataset`, `EndToEndC1Tests.LocatesFrameworkRootFromTestDirectory` | P00 §7 (R00, R01): không có manifest, `experiments/*.json`, `machine.local.json`, `litellm.config.local.yaml`; build root được dò, không đọc từ `build_attempts` |
| Tiêu chí B, screening | `Block1Tests.PairedTestStatuses`, `Block1IntegrationTests.ScreeningWritesLedgerCsvAndSkipsFinishedTasks` | P00 §7 (R01): bỏ `paired_test` và lệnh `screen` |
| Ledger, báo cáo, CSV | `RunRepositoryTests.LedgerEventsStatusControlAndLock`, `…ReportsAndLogs`, `FileHelpersTests.CsvQuotesOnlyWhenNeeded` | P00 §7 (R00, R04): bỏ ledger và báo cáo RQ |
| Quy trình prompt khác | `Block2Tests.GenerationPromptsDifferOnlyInTechniqueAndRepositoryConfiguration`, `PromptTests.StrategyMapsToTemplates`, `…CheckerDetectsUnfairChanges`, `EndToEndC1Tests.PromptsCheckPassesOnRealPrompts` | P00 §7 (R02): chỉ còn `zero_shot_project_aware`, bỏ kiểm tra công bằng RQ1 và lệnh `prompts check` |
| Fixed Repair, tập con RQ2 | `RepairPolicyTests.Section7TableFixed`, `…FixedKeepsLatestOnInvalidOutputAndRepairsFromWorseVersion`, `…SubsetTakesEqualShareOfEveryCellDeterministically`, `…SubsetTakesShortCellWholeAndRecordsShortfall`, `RepairChainIntegrationTests.FixedRebuildsDuplicateAndStopsAtBudget`, `PipelineIntegrationTests.RepairSubsetRunsInTwoPhases` | P00 §7 (R03) |
| EvoSuite | `VerifierIntegrationTests.EvoSuiteStyleSuiteIsVerifiedWithExtraClasspath`, `…MavenSystemDependencyIsAddedOnlyToCopy`, `Block4Tests.JacocoMeasuresEvoSuiteWithoutSeparateClassLoaderOnly` | P00 §7 (R03, R04) |
| Báo cáo RQ, N, P | `Block4Tests.NRuleFindsFirstFlatPairFromRoundFive`, `…PSensitivityCutsChainAfterPRoundsWithoutImprovement`, `…Rq1CountsStepsInvalidOutputAndInfra`, `…Rq2DropsUnfinishedCandidatesAndComputesCumulativeRates`, `…Rq3PairsPerMetricWithRepresentativeSuite`, `…MergingRunsWithDifferentExperimentsIsRejected`, `…NeedsRerunListsLatestInfraJobsWithCommand` | P00 §7 (R04) |
| UI console cũ | `ConsoleTests` (7), `CliIntegrationTests.HelpListsEveryCommand`, `…ScreensWorkWithRedirectedInput` | Spec 06 đã bỏ; CLI mới viết theo P08 và có test snapshot riêng ở GĐ6 |
| Build jar khi chạy | `JavaHelperBuildTests.BuildsJarFromSourcesWhenMissingOrOutdated` | P10 §2: jar nhúng sẵn, giải nén và kiểm tra sha256; cần test mới cho việc này |

## 6. Lưu ý khi viết lại

1. **Không dựa vào cấu trúc thư mục cha.** Test cũ tìm JDK ở `<cha>/Java-version/runtime/jdk-<n>` và công cụ ở `<cha>/tools`. Test mới lấy JDK từ biến `JAVA_HOME_<n>_X64` (máy dev và CI đều có), công cụ từ `ToolStore` hoặc một thư mục do test chỉ định.
2. **Không âm thầm pass khi thiếu công cụ.** `TsDetectWhenInstalled` gặp lúc thiếu tsDetect thì `return` và vẫn báo pass. Test mới phải báo rõ là bị bỏ qua, hoặc fail: dùng `Assert.Skip`/`Assert.SkipUnless` của xUnit v3 (chốt 12/11/2026, PLAN §4). Trên CI mọi công cụ đều được cài, nên ở đó không test nào được phép bị bỏ qua vì thiếu công cụ.
3. **Không ghi vào thư mục thật của người dùng.** `RemovesOnlyCorruptJarsInsideBuildCaches` tạo và xoá file trong `~/.m2/repository`. Bản mới truyền thư mục cache vào làm tham số.
4. **Thời gian CI.** Integration test cũ mất khoảng 4,5 phút trên máy dev, riêng test Gradle mất 3,5 phút (`--no-daemon`). Cần tính đến khi đặt timeout cho `ci.yml`, nhất là khi chạy trên cả Windows và Ubuntu.
5. **JDK cần cho CI:** 8 (fixture JUnit 4), 11 (`maven-multi-junit5`), 17 (Gradle, java-helper, JaCoCo/PIT/tsDetect). JDK 21 chỉ là ứng viên dự phòng, chưa test nào bắt buộc dùng.
6. **Những chỗ P-spec khác code cũ** đã đánh dấu ĐC\* ở Mục 5. Mỗi chỗ cần ghi lại trong mô tả PR của giai đoạn đó.
