# 04. Khối 4: Automated Test Suite Assessment

**Mục đích:** đo chất lượng của các bộ test **VALID** bằng ba công cụ (coverage, mutation, test smell), rồi tổng hợp mọi số liệu của 4 khối thành **Unified Metrics & Reporting** để trả lời RQ1, RQ2, RQ3 (và chọn N ở pilot).

```
Bộ test VALID (khối 3) ──┬──► 4.2 Coverage Analysis (JaCoCo) ──┐
Bộ test EvoSuite VALID ──┼──► 4.3 Mutation Analysis (PIT)  ────┼──► 4.5 Unified Metrics & Reporting
(4.1 sinh, khối 3 xác minh)└─► 4.4 Test Smell Analysis (tsDetect)┘         ▲
                                         kết quả khối 1, 2, 3 ─────────────┘
```

Chỉ đo trên bộ test VALID (Đóng góp 3: "đo lường trên tập test hợp lệ"). Mỗi bộ test được đo độc lập, trong workspace riêng.

## 1. Input

| Input | Nguồn |
| --- | --- |
| Danh sách bộ test cần đo: `suite_id`, đường dẫn mã, `test_fqn`, task | khối 3: `final.json` VALID và c0 VALID (`…__initial`); bộ EvoSuite VALID |
| M, `workspace.json`, `dependencies_resolved.txt` | khối 1 |
| Công cụ | `tools_dir` của máy: JaCoCo 0.8.12 (agent, cli), PIT 1.17.4 (command line, kèm `pitest-junit5-plugin` 1.2.1 cho JUnit 5), tsDetect 2.2, EvoSuite 1.2.0. Mỗi file có sha256 kiểm tra bằng `arrow doctor` |
| Mọi `verify_c0.json`, `decision.json`, `final.json`, `usage.json`, `outcome.json` | khối 1–3 (cho báo cáo) |

## 2. Output

| File | Nội dung |
| --- | --- |
| `block4/<suite_id>/jacoco.json` | Coverage của CUT (Mục 4.2) |
| `block4/<suite_id>/pit.json` | Kết quả mutation (Mục 4.3) |
| `block4/<suite_id>/tsdetect.json` | Test smell (Mục 4.4) |
| `block4/<suite_id>/outcome.json` | `OK` hoặc lỗi theo từng công cụ |
| `reports/*.csv`, `reports/report.md` | Unified Metrics (Mục 6) |

## 3. Flow 4.1: Bộ test của hệ thống so sánh (EvoSuite)

Không có trong hình nhưng cần cho RQ3 ("so với các hệ thống sinh test tự động khác, gồm EvoSuite").

1. Với mỗi task không `EXCLUDED`: trên một bản sao mới của `compiled_snapshot`, chạy EvoSuite 1.2.0 với **cấu hình mặc định**, `-seed 42`, `-class <CUT FQN>`, `-projectCP` = thư mục class production của module đích (`target/classes` hoặc `build/classes/java/main`) + class của các module anh em trong reactor + dependencies đã phân giải. Không quy đổi ngân sách theo thời gian của ARROW (phương án C đã chốt).
2. JDK: JDK của task nếu ≤ 11; task cần JDK > 11 → `UNSUPPORTED_JDK` (EvoSuite 1.2.0 không chạy trên Java 12+), ghi lý do, không có bộ EvoSuite cho task đó.
3. Lệnh: `java -cp evosuite-1.2.0.jar:<projectCP> org.evosuite.EvoSuite …`; timeout 20 phút. Không sinh được test (timeout, mã thoát khác 0, không có file) → `FAILED` với lý do.
4. Bộ test sinh ra (test class + scaffolding) được **xác minh bằng khối 3** (spec 03 Mục 9), không sửa. Chỉ bộ VALID được đo ở 4.2–4.4.
5. EvoSuite chỉ sinh JUnit 4. Module chỉ có JUnit 5 (không có vintage engine) hoặc chỉ có TestNG thì bộ test EvoSuite thường không biên dịch được hoặc không được phát hiện; đó là kết quả thật của công cụ, ghi lý do và báo cáo (không thêm JUnit 4 vào dự án, vì như vậy là sửa môi trường riêng cho EvoSuite).

## 4. Đo chất lượng

### 4.1. Chuẩn bị chung

- **Mỗi công cụ, mỗi bộ test chạy trên một bản sao mới** của `compiled_snapshot` (spec 01 Mục 2.3, spec 03 Mục 3.0). Lần chạy JaCoCo không để lại file ảnh hưởng tới PIT và ngược lại.
- `compiled_snapshot` được tạo **trước khi** có bộ test, nên chưa có `.class` của bộ test. Mỗi bản sao vì vậy đi qua **chuẩn bị bộ test** trước khi đo:
  1. ghi mọi file của `SuiteFiles` (spec 03 Mục 3.0: test class, scaffolding nếu là EvoSuite) và thêm `extra_test_classpath`;
  2. **biên dịch test** và lấy classpath trong **một lần gọi** từ build root với bộ chọn module: Maven `-DskipTests test-compile <dependency-plugin>:build-classpath -Dmdep.includeScope=test -Dmdep.outputFile=arrow-cp.txt`; Gradle `:<p>:testClasses` rồi init script in classpath;
  3. biên dịch thất bại → công cụ đó lỗi `B4_SUITE_COMPILE` (không xảy ra với bộ đã VALID; nếu xảy ra là lỗi môi trường, thử lại tối đa 3 lần).
  JaCoCo chạy `test` của Maven/Gradle nên tự biên dịch, nhưng vẫn theo cùng bước 1–2 để cả ba công cụ đo trên cùng một cách chuẩn bị. tsDetect chỉ đọc mã nguồn, cần bước 1, không cần bước 2.
- Lệnh build chạy từ `build_root`, Maven kèm `-pl <m> -am` như spec 03 Mục 3.1; cờ build chung (đã tắt JaCoCo của dự án bằng `-Djacoco.skip=true`).
- **Phạm vi đo là đúng CUT và các class lồng của nó**, dùng **hai mẫu chính xác** `<FQN>` và `<FQN>$*`. **Không** dùng `<FQN>*`: với CUT `com.acme.shop.Order`, mẫu `com.acme.shop.Order*` khớp nhầm cả `OrderService`, `OrderRepository`.

### 4.2. Coverage Analysis (JaCoCo)

1. Chạy **riêng** bộ test (lệnh bước 2 của spec 03 Mục 3.1, tên test lấy từ `SuiteFiles.test_fqn`, ví dụ `-Dtest=OrderService_ESTest` với bộ EvoSuite) với agent gắn qua biến môi trường:
   `JAVA_TOOL_OPTIONS += -javaagent:<jacocoagent.jar>=destfile=<exec>,append=true,includes=<FQN>:<FQN>$*`
   (JaCoCo tách nhiều mẫu bằng `:`; gắn qua biến môi trường để không xung đột với cấu hình Surefire/JaCoCo riêng của dự án).
2. Tạo báo cáo: `java -jar jacococli.jar report <exec> --classfiles <thư mục class production của module đích> --sourcefiles <source_root> --csv <file>`.
3. Đọc CSV, **chỉ lấy dòng** có cột `PACKAGE` đúng package của CUT và cột `CLASS` **bằng đúng** tên CUT hoặc bắt đầu bằng `<tên CUT>.` (JaCoCo ghi class lồng dạng `Outer.Inner`); cộng các bộ đếm. Ví dụ CUT `Order`: lấy `Order`, `Order.Item`; **không** lấy `OrderService`.
4. `jacoco.json`:
   ```json
   { "line": 0.72, "branch": 0.55, "method": 0.80,
     "counters": { "LINE_COVERED": 36, "LINE_MISSED": 14, "BRANCH_COVERED": 11, "BRANCH_MISSED": 9, "METHOD_COVERED": 8, "METHOD_MISSED": 2 } }
   ```
   Tỷ lệ = covered / (covered + missed); mẫu số 0 (ví dụ CUT không có nhánh) → `null`, không tính là 0.
5. Không có file `.exec` hoặc CUT không có trong báo cáo → `B4_JACOCO_FAILED`.

### 4.3. Mutation Analysis (PIT)

0. Chuẩn bị bộ test như Mục 4.1 (ghi file, **biên dịch test**, lấy classpath). PIT không tự biên dịch, nên thiếu bước này PIT sẽ không thấy class test.
1. Chạy PIT command line (`org.pitest.mutationtest.commandline.MutationCoverageReport`) với:
   `--classPath` = thư mục class production và class test **vừa biên dịch** của module đích + nội dung `arrow-cp.txt` của bước chuẩn bị (dependencies, class của module anh em) + thư viện PIT; `--targetClasses <FQN>,<FQN>$*` (hai mẫu chính xác, PIT coi mỗi mẫu là glob); `--targetTests <test FQN>` (bộ EvoSuite: class `*_ESTest`, không tính scaffolding); `--mutators DEFAULTS`; `--threads 1`; `--timestampedReports=false`; `--outputFormats XML`; `--sourceDirs <source_root>`.
   Module JUnit 5: thêm `pitest-junit5-plugin` 1.2.1 và `junit-platform-launcher` đúng phiên bản JUnit Platform của module. Module TestNG: thêm `pitest-testng-plugin` (phiên bản tương thích PIT 1.17.4, ghi sha256 cùng các công cụ khác).
   Sau khi chạy, kiểm tra trong `mutations.xml` mọi mutant đều thuộc `<FQN>` hoặc `<FQN>$…`; có mutant của class khác → `B4_PIT_SCOPE` (lỗi cấu hình, không ghi kết quả).
2. Đọc `mutations.xml`, đếm theo trạng thái: `KILLED`, `SURVIVED`, `NO_COVERAGE`, `TIMED_OUT`, `MEMORY_ERROR`, `RUN_ERROR`.
3. **Mutation score** = (`KILLED` + `TIMED_OUT` + `MEMORY_ERROR`) / tổng mutant (cách tính mặc định của PIT). Ghi kèm `test_strength` = killed / (tổng − `NO_COVERAGE`).
4. `pit.json`: `{ "total": 54, "killed": 30, "survived": 12, "no_coverage": 10, "timed_out": 2, "score": 0.59, "test_strength": 0.68 }`.
5. Tổng mutant = 0 → `score = null`. PIT lỗi → `B4_PIT_FAILED` kèm 20 dòng cuối log.

### 4.4. Test Smell Analysis (tsDetect)

1. Tạo file CSV đầu vào: `app,<đường dẫn test>,<đường dẫn CUT>`.
2. Chạy `java -jar TestSmellDetector.jar <csv>`; đọc CSV kết quả (một dòng cho file test, mỗi cột một loại smell).
3. `tsdetect.json`: `{ "test_methods": 6, "smells": { "Assertion Roulette": 2, "Magic Number Test": 1, … }, "total_smells": 4, "density": 0.67 }`.
4. **Test smell density** = tổng số smell / số test method (số test method đếm bằng java-helper). Báo cáo kèm tỷ lệ bộ test có từng loại smell.
5. tsDetect lỗi → `B4_TSDETECT_FAILED`.

## 5. Thứ tự và lỗi

- Ba công cụ chạy lần lượt cho mỗi bộ test; một công cụ lỗi không chặn công cụ khác. Lỗi do môi trường (timeout, công cụ không chạy) được chạy lại, tối đa 3 lần thử; vẫn lỗi thì ghi vào `outcome.json` với `needs_rerun = true` và bộ test vào danh sách "Cần chạy lại riêng" (Mục 6.6).
- Timeout: JaCoCo theo `step_timeout_seconds`; PIT 30 phút; tsDetect 5 phút.

## 6. Flow 4.5: Unified Metrics & Reporting

Lệnh `arrow report --run-id R [--run-id R2 …]` đọc kết quả của mọi khối (một hoặc nhiều run cùng experiment) và ghi `reports/`.

### 6.1. Verification metrics (RQ1, Đóng góp 1 và 3)

Đơn vị: candidate c0 (một lần sinh). Mẫu số: mọi candidate của task không `EXCLUDED`, kể cả `INVALID_OUTPUT` (tính là không qua bước nào); trừ candidate `INFRA` (liệt kê riêng).

| Chỉ số | Tử số: candidate đạt ít nhất tới |
| --- | --- |
| Compilation success rate | qua bước 1 (state ≥ DISCOVERY) |
| Test discovery rate | qua bước 2 (state ≥ RUNTIME) |
| Execution success rate | qua bước 3 (state ≥ ASSERTION) |
| Target test pass rate | qua bước 4 (state ≥ REGRESSION) |
| Module regression (không regression) | VALID |

Bảng theo `dataset × model × strategy`; Classes2Test (content-matched) và GitBug-Java (upstream-pinned) **tách riêng**. Kèm bảng error signature thường gặp, số output không hợp lệ, số lượt `finish_reason = length`.

`reports/rq1_verification.csv`: `dataset,model,strategy,candidates,compile,discovery,execution,target_pass,valid,invalid_output,infra`.

### 6.2. Repair effectiveness (RQ2)

Đơn vị: chain. Chỉ tính candidate mà **mọi cơ chế** đều chạy xong (không INFRA).

| Chỉ số | Định nghĩa |
| --- | --- |
| Repair success rate | chain `success` / số chain |
| Number of repair attempts | trung bình và trung vị `success_round` (chain không thành công tính N) |
| Repair time | trung bình và trung vị `repair_seconds` |

Báo cáo kèm (mô tả, không chứng minh nhân quả): tỷ lệ thành công cộng dồn theo vòng k = 1…N cho từng cơ chế; bảng độ nhạy P (Mục 6.4); tổng số lần rollback, mã trùng, output không hợp lệ, lỗi lặp, lên mức, đổi loại lỗi, số vòng mức 3; số test method bị bỏ ở mức 3; thành công theo trạng thái c0.

`reports/rq2_chains.csv` (mỗi chain một dòng, các trường của `final.json`), `reports/rq2_summary.csv`, `reports/rq2_cumulative.csv` (`mechanism,round,successes_cumulative,rate`).

### 6.3. Chọn N (chỉ ở pilot)

Pilot chạy `n = 10`. Gộp chain của **cả hai cơ chế** trên Classes2Test, S(k) = số chain đạt VALID tại vòng ≤ k. Quy tắc (CLAUDE.md, chốt 09/10/2026): N = vòng k nhỏ nhất với 5 ≤ k ≤ 10 mà S(k−1) = S(k−2) và S(k) = S(k−1) (cả vòng k−1 và vòng k không có thêm chain thành công); không có k nào thỏa thì N = 10. Báo cáo kèm bảng S(k) và tỷ lệ S(k)/S(10). Pilot không xem kết quả so sánh hai cơ chế.

`reports/pilot_n.csv`: `round,successes_cumulative,share_of_round_n`.

### 6.4. Độ nhạy P (dừng sớm, chỉ Adaptive)

Từ `decision.json` của mỗi chain Adaptive, tính lại kết quả nếu dừng sau P vòng liên tiếp không có bản tốt hơn checkpoint (`improved = false`), với P = 1, 2, 3: tỷ lệ thành công, số vòng trung bình, thời gian trung bình. Không gọi thêm model.

### 6.5. Quality metrics (RQ3)

Đơn vị: bộ test VALID. Bảng theo hệ thống:

| Hệ thống | Bộ test |
| --- | --- |
| ARROW (mỗi task, mỗi model tối đa **một** bộ) | Candidate `zero_shot_project_aware`: bộ `…__initial` nếu VALID ngay khi sinh; nếu không thì bộ `…__adaptive` nếu chain Adaptive đạt VALID *(chốt Q3, 14/10/2026)* |
| EvoSuite | `evosuite` |
| Các bộ khác của ARROW | báo cáo kèm theo `strategy × mechanism`, không dùng để kết luận RQ3 |

Chỉ số: line, branch, method coverage; mutation score; test smell density — trung bình, trung vị, độ lệch chuẩn. Bảng **tách riêng từng model và từng dataset** (chốt Q3).

**Ghép cặp theo từng chỉ số.** Mỗi công cụ có thể lỗi độc lập (ví dụ ARROW đo được coverage nhưng PIT lỗi, EvoSuite đo được cả hai). Nếu lấy trung bình trên dữ liệu còn lại của mỗi bên thì hai bên được so trên **hai tập task khác nhau**. Vì vậy với **mỗi chỉ số** riêng:

1. Tập so sánh = các task mà **cả** bộ ARROW **và** bộ EvoSuite đều VALID **và** đều đo được chỉ số đó (giá trị khác `null`, công cụ không lỗi).
2. Tính trung bình, trung vị, chênh lệch theo cặp trên đúng tập đó; báo `n_pairs` của chỉ số đó.
3. Báo kèm: số task chỉ ARROW có bộ VALID, chỉ EvoSuite có, cả hai không có; và số cặp bị loại khỏi từng chỉ số vì công cụ lỗi hay giá trị `null` (ví dụ CUT không có nhánh thì branch coverage `null`).

Ví dụ: 30 task cả hai có bộ VALID; JaCoCo lỗi ở 1 task phía ARROW; PIT lỗi ở 2 task phía EvoSuite; 4 CUT không có nhánh → `n_pairs`: line 29, method 29, branch 25, mutation 28, smell 30.

`reports/rq3_quality.csv` (mỗi bộ test một dòng), `reports/rq3_summary.csv`.

### 6.6. Chi phí và tài nguyên

Theo khối và dataset: số lượt gọi model, token vào/ra, chi phí USD (giá trong experiment; lượt phát lại khi chạy tiếp dùng số liệu của lần gọi thật và chỉ tính một lần), thời gian trung vị/p90/max của các bước build, gọi model, EvoSuite, JaCoCo, PIT, tsDetect. Danh sách task `EXCLUDED` kèm lý do.

**Cần chạy lại riêng** (`reports/needs_rerun.csv`, chốt Q8): mỗi job `INFRA` một dòng `task_id, dataset, block, job, step, error_code, attempts, seconds_each, timeout_repeated, rerun_command`, ví dụ:

```
555001_ab12cd34ef56ab78,classes2test,3,gpt-6-luna/zero_shot/adaptive,run_target,B3_TIMEOUT,3,"900;900;900",false,arrow run --run-id run-1 --tasks 555001_ab12cd34ef56ab78 --rerun-infra
```

Báo cáo nêu rõ các job này **chưa có kết quả** và chưa được tính vào RQ1–RQ3; danh sách rỗng mới là run hoàn chỉnh.

### 6.7. `report.md`

Thứ tự mục: thông tin run (experiment, sha256, máy, commit tool, model id trả về) → kết quả khối 1 (task dùng được/loại) → 6.1 → 6.2 (hoặc 6.3 ở pilot) → 6.4 → 6.5 → 6.6 → ngoại lệ đã khai báo (cờ build chung, locale JVM, runtime EvoSuite trong classpath test, cách gắn JaCoCo) → hạn chế. Số liệu trong `report.md` đọc từ chính các CSV ở trên.

### 6.8. Gộp nhiều run

Kiểm tra trước khi gộp: cùng sha256 experiment và prompt; cùng model id trả về; không có task trùng giữa các run. Lệch thì báo lỗi và không ghi báo cáo.

## 7. Mã lỗi

| Mã | Khi nào |
| --- | --- |
| `B4_EVOSUITE_UNSUPPORTED_JDK` | Task cần JDK > 11 |
| `B4_EVOSUITE_FAILED` | EvoSuite không sinh được test |
| `B4_JACOCO_FAILED` | Không có dữ liệu coverage của CUT |
| `B4_PIT_FAILED` | PIT lỗi |
| `B4_TSDETECT_FAILED` | tsDetect lỗi |
| `B4_PIT_SCOPE` | PIT tạo mutant cho class ngoài CUT và class lồng (lỗi cấu hình) |
| `B4_SUITE_COMPILE` | Không biên dịch được bộ test trong bản sao dùng để đo (3 lần thử) |
| `B4_REPORT_MISMATCH` | Gộp các run khác cấu hình |

## 8. Interface C#

```csharp
public interface IComparisonGenerator      // EvoSuite (Flow 4.1)
{
    Task<ComparisonSuite> GenerateAsync(TaskContext task, CancellationToken ct);
}

public interface IQualityTool              // JacocoTool, PitTool, TsDetectTool (Flow 4.2–4.4)
{
    string Name { get; }
    Task<ToolResult> MeasureAsync(SuiteToMeasure suite, TaskContext task, CancellationToken ct);
}

public interface IReportBuilder            // Flow 4.5, code thuần trên dữ liệu đã đọc
{
    UnifiedReport Build(IReadOnlyList<RunResults> runs, ExperimentConfig experiment);
}
```

## 9. Kiểm thử

- Unit: đọc CSV JaCoCo có class lồng và class tên gần giống (`Order` / `OrderService`); ghép cặp RQ3 theo từng chỉ số với `n_pairs`; đếm `mutations.xml`; đọc CSV tsDetect; công thức 6.1–6.4 trên dữ liệu mẫu (kể cả quy tắc N có và không có k thỏa); bỏ chain chưa xong khỏi RQ2.
- Integration: đo một bộ test VALID trên mỗi fixture (JUnit 4 Maven, JUnit 5 Maven đa module, JUnit 5 Gradle); EvoSuite sinh bộ VALID trên fixture JUnit 4.

## 10. Ví dụ (CUT minh họa, tiếp theo spec 03 Mục 14)

### 10.1. Input: danh sách bộ test VALID của task `555001_…` (`OrderService`)

| `suite_id` | Từ đâu | Có trong RQ3 chính? |
| --- | --- | --- |
| `gpt-6-luna__zero_shot_project_aware__initial` | c0 của `zero_shot_project_aware` VALID ngay (spec 03 Mục 14.2) | **Có** (bộ đại diện ARROW, Q3) |
| `gpt-6-luna__zero_shot__adaptive` | chain Adaptive của `zero_shot` thành công ở vòng 5 (spec 03 Mục 14.5) | Không (báo cáo kèm) |
| `evosuite` | EvoSuite sinh 18 test, qua 5 bước xác minh → VALID | **Có** (đối chứng) |

(Chain Fixed của `zero_shot` không VALID nên không có bộ để đo.)

### 10.2. Flow chạy từng bước cho bộ `gpt-6-luna__zero_shot_project_aware__initial`

| Bước | Việc framework làm | Kết quả ví dụ |
| --- | --- | --- |
| 4.1 Chuẩn bị | Workspace mới từ workspace gốc; ghi `OrderService_ArrowTest.java` vào test path | |
| 4.2 JaCoCo | Bản sao mới của snapshot; chạy riêng `OrderService_ArrowTest` với `-javaagent:jacocoagent.jar=destfile=…/jacoco.exec,includes=com.acme.shop.OrderService:com.acme.shop.OrderService$*` | Có `jacoco.exec` |
| 4.2 Báo cáo | `java -jar jacococli.jar report jacoco.exec --classfiles shop-core/target/classes --sourcefiles shop-core/src/main/java --csv jacoco.csv` | Lấy dòng `OrderService` và `OrderService.Item`; bỏ dòng `OrderServiceImpl` nếu có |
| 4.3 PIT | Bản sao mới; ghi `OrderService_ArrowTest.java`; `test-compile …:build-classpath` (một lần gọi); `--targetClasses com.acme.shop.OrderService,com.acme.shop.OrderService$* --targetTests com.acme.shop.OrderService_ArrowTest --mutators DEFAULTS --threads 1` | 54 mutant, đều thuộc `OrderService` / `OrderService$Item` ✔ |
| 4.4 tsDetect | CSV đầu vào `app,…/OrderService_ArrowTest.java,…/OrderService.java` → chạy `TestSmellDetector.jar` | 2 loại smell |

### 10.3. Output của bộ đó

```json
// block4/gpt-6-luna__zero_shot_project_aware__initial/jacoco.json
{ "line": 0.72, "branch": 0.55, "method": 0.80,
  "counters": { "LINE_COVERED": 36, "LINE_MISSED": 14, "BRANCH_COVERED": 11, "BRANCH_MISSED": 9, "METHOD_COVERED": 8, "METHOD_MISSED": 2 } }

// pit.json — score = (30 killed + 2 timed out) / 54 = 0.59; test_strength = 30 killed / (54 − 10 no coverage) = 0.68
{ "total": 54, "killed": 30, "survived": 12, "no_coverage": 10, "timed_out": 2, "score": 0.59, "test_strength": 0.68 }

// tsdetect.json — density = 4 smell / 5 test method = 0.8
{ "test_methods": 5, "smells": { "Assertion Roulette": 3, "Magic Number Test": 1 }, "total_smells": 4, "density": 0.8 }
```

### 10.4. Ví dụ dòng trong báo cáo

`reports/rq1_verification.csv` (một dòng = một nhóm candidate):

```
dataset,model,strategy,candidates,compile,discovery,execution,target_pass,valid,invalid_output,infra
classes2test,gpt-6-luna,zero_shot,7,1,1,1,0,0,0,0
classes2test,gpt-6-luna,zero_shot_project_aware,7,4,4,4,3,3,0,0
```

Đọc: với `zero_shot_project_aware`, 4/7 candidate biên dịch được (57%), 3/7 VALID (43%).

`reports/rq2_summary.csv`:

```
dataset,model,mechanism,chains,success,success_rate,attempts_mean,attempts_median,time_mean_s,time_median_s
classes2test,gpt-6-luna,fixed,17,12,0.71,2.9,2,41.0,27.5
classes2test,gpt-6-luna,adaptive,17,14,0.82,2.6,2,38.2,25.1
```

`reports/rq3_quality.csv` (một dòng = một bộ test):

```
dataset,model,task_id,system,suite_id,line,branch,method,mutation_score,smell_density
classes2test,gpt-6-luna,555001_ab12cd34ef56ab78,ARROW,gpt-6-luna__zero_shot_project_aware__initial,0.72,0.55,0.80,0.59,0.80
classes2test,gpt-6-luna,555001_ab12cd34ef56ab78,EvoSuite,evosuite,0.86,0.70,0.90,0.48,1.40
```

`reports/rq3_summary.csv` (mỗi chỉ số một dòng, có `n_pairs` riêng):

```
dataset,model,metric,n_pairs,arrow_mean,evosuite_mean,arrow_median,evosuite_median,mean_diff
classes2test,gpt-6-luna,line,29,0.71,0.83,0.74,0.86,-0.12
classes2test,gpt-6-luna,branch,25,0.52,0.69,0.55,0.70,-0.17
classes2test,gpt-6-luna,mutation_score,28,0.55,0.47,0.57,0.45,0.08
```

Số liệu trên chỉ để minh họa định dạng, không phải kết quả thật.

### 10.5. Ví dụ lỗi khi đo

| Tình huống | Kết quả |
| --- | --- |
| Không có `jacoco.exec` (test không chạy dưới agent) | `B4_JACOCO_FAILED`; PIT và tsDetect vẫn chạy; bộ test có mặt trong RQ3 nhưng thiếu số coverage, ghi rõ |
| PIT báo `No mutations found` (CUT chỉ có getter đơn giản) | `total = 0`, `score = null` (không tính là 0) |
| Task cần JDK 17 | EvoSuite `B4_EVOSUITE_UNSUPPORTED_JDK`; task đó không có cặp ARROW–EvoSuite, được đếm vào "chỉ ARROW có bộ VALID" |

## Bổ sung 15/10/2026: tập con RQ2 và chain chỉ cho RQ3

- Bảng RQ2 (`rq2_*.csv`) và quy tắc chọn N **bỏ** các chain có `rq3_only: true` (spec 03 Mục 6.1): RQ2 chỉ tính ứng viên của tập con cố định (Classes2Test) hoặc mọi ứng viên thất bại (dataset không đặt `repair_subset`).
- Bộ test ARROW đại diện của RQ3 (Q3) vẫn là `<model>__zero_shot_project_aware__initial`, nếu không có thì `<model>__zero_shot_project_aware__adaptive`; chain `rq3_only` cung cấp bộ thứ hai cho CUT ngoài tập con.

## Bổ sung 15/10/2026: đo JaCoCo trên bộ EvoSuite

Bộ EvoSuite sinh với cấu hình mặc định có `@EvoRunnerParameters(… separateClassLoader = true)`: CUT được nạp trong classloader riêng từ bytecode EvoSuite đã biến đổi, nên JaCoCo (agent) không khớp được class với file `.class` gốc và báo coverage 0 (chạy thử với model thật: line 0 trong khi PIT diệt 12/15 mutant). Ngược lại, với `separateClassLoader = false`, runtime của EvoSuite biến đổi lại class của CUT và PIT mất dữ liệu coverage (15/15 mutant `NO_COVERAGE`). Vì vậy:
- EvoSuite vẫn chạy cấu hình mặc định; xác minh (khối 3) và PIT dùng **bộ test gốc**.
- **Chỉ khi đo JaCoCo**, bản sao của bộ test đổi annotation thành `separateClassLoader = false` (test method giữ nguyên từng chữ) và JVM chạy test có `-Djdk.attach.allowAttachSelf=true` (runtime của EvoSuite tự gắn agent; từ JDK 9 phải cho phép).
- Ngoại lệ đã khai báo, giống nhau cho mọi bộ EvoSuite; bộ ARROW không chứa annotation này nên không bị ảnh hưởng. Kết quả sau khi sửa trên CUT `118477931`: line 100%, branch 100%, mutation 0,8.

