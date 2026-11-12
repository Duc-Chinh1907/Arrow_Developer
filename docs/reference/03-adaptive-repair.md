# 03. Khối 3: Adaptive Repair Strategy

**Mục đích:** biên dịch và chạy candidate T (xác minh 5 bước của Đóng góp 3); T đạt thì chuyển sang khối 4; T không đạt thì chẩn đoán lỗi, quyết định có sửa tiếp không, và nếu có thì tạo yêu cầu sửa gửi ngược về khối 2 (vòng lặp nét đứt). Hết lượt sửa thì chốt bộ test.

Thuật toán sửa theo `ARROW/Adaptive repair.md` (viết tắt **AR**). Spec này ghi rõ input/output và từng flow để code; chỗ nào khác hoặc làm rõ AR thì ghi trong mục "Chờ xác nhận".

```
          T (từ khối 2)
              │
   ┌──────────▼───────────┐
   │ 3.1 Compile → Run    │  xác minh 5 bước
   └──────────┬───────────┘
        Success? ──Yes──► 3.2 Execution Passed ──────────────────────────► khối 4
              │No
   ┌──────────▼───────────┐
   │ 3.3 Failure Diagnosis │  trạng thái, signature, hash, method lỗi
   └──────────┬───────────┘
   Can Repair Continue? ──No──► 3.6 Finalize Test Suite ─────────────────► khối 4 (chỉ bộ VALID được đo)
              │Yes
   ┌──────────▼────────────────┐
   │ 3.5 Repair Algorithm      │  Adaptive (hoặc Fixed để đối chứng)
   └──────────┬────────────────┘
              └── RepairRequest ──► khối 2 (chế độ REPAIR) ──► T mới ──► 3.1
```

## 1. Input

| Input                                                        | Nguồn                                                       |
| ------------------------------------------------------------ | ------------------------------------------------------------ |
| Candidate T: `candidate.java`, `candidate.json`          | khối 2                                                      |
| Repository Metadata M, `baseline.json`, `workspace.json` | khối 1                                                      |
| `mechanism`                                                | `fixed` \| `adaptive` (experiment `repair.mechanisms`) |
| `N`                                                        | experiment `repair.n`                                      |
| `repair` của manifest                                     | `false` → chỉ xác minh, không sửa                     |
| Cờ build, JDK, timeout                                      | `workspace.json`, experiment `verification`              |

## 2. Output

| File                                         | Nội dung                                                                     |
| -------------------------------------------- | ----------------------------------------------------------------------------- |
| `block3/<model>/<strategy>/verify_c0.json` | Kết quả xác minh candidate ban đầu (dùng cho RQ1)                       |
| `…/<mechanism>/round_<k>/verify.json`     | Kết quả xác minh vòng k (Mục 3.7)                                        |
| `…/<mechanism>/round_<k>/decision.json`   | Quyết định của thuật toán ở vòng k (Mục 6.6)                         |
| `…/<mechanism>/final.json`                | **Final Test Suite** của chain (Mục 8)                                |
| `…/<mechanism>/final.java`                | Mã bộ test cuối (VALID, hoặc checkpoint/bản mới nhất khi không VALID) |

## 3. Flow 3.1: Compilation and Execution (xác minh 5 bước)

### 3.0. Workspace sạch cho **mỗi lần xác minh**

Một chain có nhiều vòng; test của vòng trước có thể xóa resource, ghi file, đổi file cấu hình trong workspace. Nếu dùng lại workspace, vòng sau (và cả bản checkpoint được rollback về) có thể hỏng vì hậu quả của vòng trước. Vì vậy:

1. **Mỗi lần xác minh** (c0, mỗi vòng sửa của Fixed và Adaptive, bộ EvoSuite) đều chạy trên một **bản sao mới** của `compiled_snapshot` của task (spec 01 Mục 2.3: mã đúng commit, đã biên dịch, **chưa chạy test nào**). Không dùng lại workspace của vòng trước.
2. Ghi **mọi file của bộ test** (`SuiteFiles`, dưới) vào bản sao đó, thêm classpath phụ nếu có, rồi chạy 5 bước dưới đây.

**`SuiteFiles`: mô tả một bộ test cần xác minh hoặc đo**, dùng chung cho candidate của ARROW và bộ EvoSuite. Lệnh chạy test **luôn lấy tên từ `test_fqn` của bộ**, không tự ghép hậu tố:

| Trường                                                                                                                                                                                                                  | Candidate ARROW                          | Bộ EvoSuite                                                                 |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- | ---------------------------------------------------------------------------- |
| `test_fqn`                                                                                                                                                                                                              | `com.acme.shop.OrderService_ArrowTest` | `com.acme.shop.OrderService_ESTest`                                        |
| `files` (đường dẫn tính từ gốc repository → nội dung)                                                                                                                                                          | `…/OrderService_ArrowTest.java`       | `…/OrderService_ESTest.java`, `…/OrderService_ESTest_scaffolding.java` |
| `extra_test_classpath`                                                                                                                                                                                                  | –                                       | `evosuite-standalone-runtime-1.2.0.jar`                                    |

3. Xong lần xác minh (kể cả khi lỗi): đọc báo cáo, chép log và XML cần giữ vào thư mục kết quả của vòng, rồi **xóa** bản sao (trừ khi `keep_failed_workspaces` bật để gỡ lỗi).
4. Chạy lại một chain (sau INFRA hoặc khi chạy tiếp) cũng bắt đầu từ bản sao mới, không dùng workspace còn sót.
5. Sao chép dùng cách nhanh nhất hệ điều hành hỗ trợ (copy-on-write/clonefile trên macOS APFS, reflink trên Linux nếu có, nếu không thì copy thường). Thời gian sao chép ghi vào `build_seconds`.

### 3.1. Năm bước

**Lệnh chạy từ `build_root`, Maven luôn kèm đúng bộ chọn module của spec 01 Mục 4.1** (`-f pom.xml`; `-pl <module> -am` ở chế độ reactor, không có ở chế độ standalone) ở **mọi** bước. `<Tên>` dưới đây là **tên ngắn của `SuiteFiles.test_fqn`** (`OrderService_ArrowTest`, `OrderService_ESTest`); khối 1 kiểm tra tên ngắn này không trùng test class sẵn có nào trong reactor (trùng thì dùng mẫu đường dẫn `com/acme/shop/OrderService_ESTest`).

| Bước               | Lệnh Maven                                                                                                                                         | Lệnh Gradle                                | Đạt khi                                                                              | Không đạt                             |
| -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | -------------------------------------------------------------------------------------- | ---------------------------------------- |
| 1. Compile           | `-f pom.xml [-pl <m> -am] … -DskipTests test-compile`                                                                                            | `:<p>:testClasses`                        | Mã thoát 0                                                                           | `COMPILE` hoặc `UNKNOWN` (Mục 3.2) |
| 2. Discovery         | `-f pom.xml [-pl <m> -am] … -Dtest=<Tên> -Dsurefire.failIfNoSpecifiedTests=false -DfailIfNoTests=false -Dmaven.test.failure.ignore=true test`   | `:<p>:test --tests <test_fqn> --continue` | Báo cáo XML có ≥ 1 test method của class `test_fqn`                             | `DISCOVERY`                            |
| 3. Execution         | (cùng lần chạy bước 2)                                                                                                                         |                                             | Không test method nào lỗi bằng exception không phải assertion                    | `RUNTIME`                              |
| 4. Target pass       | (cùng lần chạy bước 2)                                                                                                                         |                                             | Mọi test method pass                                                                  | `ASSERTION`                            |
| 5. Module regression | `-f pom.xml [-pl <m> -am] … -DfailIfNoTests=false -Dmaven.test.failure.ignore=true test` (**cùng lệnh** với baseline, spec 01 Mục 6.2) | `:<p>:test --continue`                    | Mọi test trong `baseline.passing_in_all_runs` vẫn pass, và test sinh ra vẫn pass | `REGRESSION`                           |

- Bước 2 và bước 5 chạy trên **hai bản sao mới khác nhau** của snapshot (bước 5 không thấy file do lần chạy bước 2 để lại). Bước 2 dùng lại bản sao vừa biên dịch ở bước 1 (bản sao đó chưa chạy test nào); khi phải thử lại vì lỗi hạ tầng thì dùng bản sao mới.
- Lệnh chạy riêng ở bước 2 thất bại mà không có báo cáo của class `test_fqn` và không phải thông báo "không tìm thấy test" (`No tests found for given includes`, `No tests were executed`), ví dụ JVM chạy test bị sập hoặc `System.exit`, → `RUNTIME` với signature `RUNTIME | <plugin hoặc task lỗi> | -` (bổ sung khi code C4).
- `-Dsurefire.failIfNoSpecifiedTests=false` để các module anh em trong reactor (không có class `<Tên>`) không báo lỗi "No tests matching".
- Scaffolding của EvoSuite không phải test class; chỉ class `test_fqn` được tính ở bước 2–4.
- Với `-am`, bước 5 cũng chạy test của module anh em, đúng như baseline; so sánh regression dùng chung danh sách nên nhất quán (spec 01 Mục 6).
- Trên Windows thêm `-Dsurefire.useManifestOnlyJar=false` (cờ chung).

Đạt cả 5 bước → `VALID`. Cờ build chung, JDK, `JAVA_TOOL_OPTIONS` giống baseline.

### 3.2. Phân biệt COMPILE và UNKNOWN ở bước 1

1. Tìm dòng lỗi javac trong log bằng biểu thức nhận được mọi dạng đường dẫn:
   `^(?:\[ERROR\]\s+)?(?<file>(?:/?[A-Za-z]:)?[^:\[\]\r\n]*?\.java):(?:\[(?<line>\d+),(?<col>\d+)\]|(?<line2>\d+):\s*error:)\s*(?<msg>.*)$`
   Nhận đúng `/Users/a/X.java:[13,6]` (macOS), `/D:/a/X.java:[13,6]` (Maven trên Windows), `D:\a\X.java:13: error:` (Gradle trên Windows). Kèm dòng `symbol:` / `location:` ngay sau.
2. Có ít nhất một lỗi javac **trong file test sinh ra** (so theo tên file) → `COMPILE`. "File sinh ra" là file của class `test_fqn` và các file của bộ có cùng tiền tố tên (scaffolding `…_ESTest_scaffolding.java` của EvoSuite).
3. Build thất bại nhưng không có lỗi javac nào trong file sinh ra → `UNKNOWN`.
4. Không tính là lỗi javac: dòng chứa `AssertionError`, dòng trong stack trace (`at …`), cảnh báo `warning:`.
5. *(15/10/2026)* maven-compiler-plugin 2.x in lỗi bị cụt: thông điệp có tiền tố `error:` (`X.java:[65,44] error: cannot find symbol`, tiền tố bị bỏ), mất dòng `symbol:`, dòng `location:` còn lại không có nhãn (`[ERROR]  class Bounds`) → nhận là `location`. Pilot: task `149270308` (plugin 2.3.2) có `symbol`/`location` rỗng nên mức 2 không đưa được API của `Bounds` và cả 6 chain kẹt ở COMPILE.

### 3.3. Phân loại kết quả chạy test (bước 2–4)

1. Đọc mọi `TEST-*.xml` của class sinh ra. Tên class lấy từ thuộc tính `classname`; nếu không phải tên class đầy đủ thì lấy theo tên testsuite. Bỏ `()` ở cuối tên method (Gradle JUnit 5).
2. **Tên exception** của một test lỗi: thuộc tính `type` nếu là tên class Java hợp lệ (`a.b.C`); nếu không (một số plugin ghi thông điệp vào `type`) thì lấy tên exception ở dòng đầu stack trace, hoặc dòng `Caused by:` đầu tiên.
3. **Exception assertion**: `java.lang.AssertionError`, `org.opentest4j.AssertionFailedError`, `org.junit.ComparisonFailure`, `junit.framework.AssertionFailedError`, `org.opentest4j.MultipleFailuresError`, mọi lớp trong `org.assertj.core.error.*`, `org.mockito.exceptions.verification.*`, `org.hamcrest.*` assertion; và lỗi của `assertThrows`/`@Test(expected=…)` ("Expected … to be thrown").
4. Không có test method nào được chạy, hoặc JUnit 4 báo `initializationError` "No runnable methods" → `DISCOVERY`.
5. Có ít nhất một test method lỗi bằng exception **không** phải assertion, hoặc class lỗi khi khởi tạo → `RUNTIME`.
6. Mọi test method lỗi đều là exception assertion → `ASSERTION`.

### 3.4. Module regression (bước 5)

- Test sẵn có trong `passing_in_all_runs` mà fail hoặc **không còn chạy** (mất khỏi báo cáo) → `REGRESSION`.
- Test sinh ra pass khi chạy riêng nhưng fail khi chạy cùng module (phụ thuộc thứ tự, trạng thái dùng chung) → `REGRESSION`.
- Test `flaky` ở baseline không được dùng để kết luận.
- Test sinh ra **không được chạy** ở bước 5 (ví dụ dự án cấu hình `includes` của Surefire khác mẫu tên mặc định) không bị coi là regression; chỉ khi nó chạy và fail cùng module (Mục 15.1 (c)).

### 3.5. Lỗi hạ tầng

*(Chốt Q8, 14/10/2026.)* Lỗi hạ tầng gồm: timeout một bước (`step_timeout_seconds`, áp dụng cho bước biên dịch, lần chạy riêng test sinh ra và lần chạy cả module), lỗi tải dependency, công cụ không chạy được (mã thoát không có, tiến trình bị giết), hết đĩa.

1. Gặp lỗi hạ tầng → **chạy lại đúng bước đó**, tối đa **3 lần thử** (`verification.infra_attempts = 3`), chờ 30 s giữa hai lần.
2. Lần thử thứ 3 vẫn lỗi → bước đó nhận `INFRA` với mã `B3_TIMEOUT`, `B3_DEPENDENCY` hoặc `B3_TOOL_ERROR`, kèm `needs_rerun: true` và lý do (bước nào, mấy lần, mỗi lần bao lâu).
3. Job được ghi vào **danh sách "Cần chạy lại riêng"** (`reports/needs_rerun.csv` và một mục trong `report.md`), mỗi dòng có lệnh chạy lại riêng task đó:
   `arrow run --run-id <run> --tasks <task_id> --rerun-infra`
4. Lệnh trên chỉ chạy lại các job `INFRA` của task đó (từ bước bị lỗi; chain sửa thì từ đầu chain), không chạy lại job đã xong.

Lỗi hạ tầng không thuộc chuỗi trạng thái, không phải lỗi của candidate:

- ở c0: candidate chưa có kết quả, **chưa** tính vào RQ1/RQ2 cho tới khi chạy lại xong;
- giữa chain: chain dừng với `outcome = INFRA`, chạy lại từ đầu chain (lượt gọi model đã có trong cache không tốn tiền); RQ2 chỉ tính candidate mà mọi cơ chế đều chạy xong.

**Timeout lặp lại khi chạy lại riêng:** nếu sau lệnh chạy lại riêng mà cùng bước vẫn timeout đủ 3 lần, báo cáo đánh dấu `timeout_repeated: true` cho job đó (gợi ý test sinh ra có thể tự treo, ví dụ vòng lặp vô hạn). Framework **không** tự đổi trạng thái; nhóm xem log và quyết định (spec 05 Q8).

### 3.6. Số liệu đo kèm

- `tests_passed`: số test method của class sinh ra pass ở lần chạy riêng (bước 2–4).
- `assertion_count`: đếm qua AST (java-helper), quy tắc ở AR Mục 5.2 (JUnit/TestNG, AssertJ/Hamcrest `assertThat`, `@Test(expected)`, `fail()` trong `try`, Mockito `verify` (cả `inOrder.verify`), `verifyNoInteractions`/`verifyNoMoreInteractions`/`verifyZeroInteractions`, kiểu BDD `then(mock).should…()` và `then(actual).is…()`, MockMvc `andExpect(…)`, `thrown.expect…(…)` của `ExpectedException`, kiểm tra RestAssured; không đếm assertion tầm thường; bổ sung 15/10/2026).
- `build_seconds`: tổng thời gian các lệnh build của lần xác minh.

### 3.7. `verify.json`

```json
{
  "state": "ASSERTION", "group": "ASSERTION", "infra_reason": null,
  "steps": [ { "step": "compile", "ok": true, "log": "logs/…", "seconds": 8.1 },
             { "step": "run_target", "ok": false, "log": "logs/…", "seconds": 6.3 } ],
  "test_methods": { "total": 5, "passed": 3, "failed": 2 },
  "failures": [ { "method": "resolvesRelative", "type": "org.opentest4j.AssertionFailedError", "message": "expected: <a> but was: <b>", "trace_head": "…" } ],
  "compile_errors": [],
  "regressions": [],
  "build_error_excerpt": null,
  "test_file": "src/test/java/…/PathUtil_ArrowTest.java",
  "error_signature": "ASSERTION | AssertionFailedError | resolvesRelative",
  "failing_methods": [ "resolvesRelative", "rejectsNull" ],
  "code_hash": "sha256:…", "assertion_count": 9, "build_seconds": 14.4
}
```

## 4. Flow 3.2: Success? / Execution Passed

`state = VALID` → ghi `final.json` với `success = true` (Mục 8) và đưa bộ test vào danh sách đo của khối 4. Với c0 VALID: không chạy chain sửa; bộ test có `suite_id = <model>__<strategy>__initial`.

## 5. Flow 3.3: Failure Diagnosis

Từ `verify.json`, dựng `Diagnosis` dùng cho quyết định và prompt:

| Trường            | Cách tính                                                                                                                                                             |
| ------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `state`           | Mục 3.2–3.4                                                                                                                                                           |
| `group`           | `BUILD` (COMPILE, UNKNOWN, DISCOVERY), `RUNTIME` (RUNTIME, REGRESSION), `ASSERTION` (ASSERTION). Chỉ để mô tả và báo cáo, không dùng để quyết định |
| `error_signature` | Bảng dưới                                                                                                                                                            |
| `code_hash`       | từ khối 2                                                                                                                                                             |
| `failing_methods` | COMPILE: method chứa dòng lỗi javac (java-helper `method_at_line`); RUNTIME/ASSERTION: method fail trong báo cáo; trạng thái khác: rỗng                      |
| `error_block`     | Phần lỗi đưa vào prompt (dưới)                                                                                                                                   |

**Error signature** (chuẩn hóa, bỏ số dòng, đường dẫn, tên biến, giá trị; lấy lỗi đầu tiên, ưu tiên lỗi nằm trong file sinh ra):

| Trạng thái | Dạng                                                                                     | Ví dụ                                                      |
| ------------ | ----------------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| COMPILE      | `COMPILE \| <loại javac> \| <loại symbol>`                                              | `COMPILE \| cannot find symbol \| method`                    |
| UNKNOWN      | `UNKNOWN \| <plugin hoặc task lỗi> \| -`                                                | `UNKNOWN \| maven-surefire-plugin \| -`                      |
| DISCOVERY    | `DISCOVERY \| no tests executed \| -`                                                     |                                                              |
| RUNTIME      | `RUNTIME \| <tên exception ngắn> \| <Class.method của frame đầu thuộc mã dự án>` | `RUNTIME \| NullPointerException \| OrderService.totalPrice` |
| ASSERTION    | `ASSERTION \| <tên exception ngắn> \| <test method fail đầu tiên>`                   | `ASSERTION \| AssertionFailedError \| testGetOneNotFound`    |
| REGRESSION   | `REGRESSION \| existing tests fail \| <các test class sẵn có bị fail, sắp xếp>`     | `REGRESSION \| existing tests fail \| ATest,BTest`           |

Loại javac quy về danh sách cố định: `cannot find symbol`, `incompatible types`, `unreported exception`, `method cannot be applied`, `constructor cannot be applied`, `package does not exist`, `has private access`, `is not public`, `cannot be referenced from a static context`, `already defined`, `missing return statement`, `unreachable statement`, `class is abstract`, `does not override`, `cannot be accessed`, `other`.

**Error block** (tối đa 60 dòng, bỏ đường dẫn tuyệt đối của workspace):

- COMPILE: các lỗi javac trong file sinh ra, mỗi lỗi kèm `symbol`/`location` và dòng mã tương ứng.
- UNKNOWN: khối `Failed to execute goal` / `What went wrong` kèm các dòng `Caused by:`, bỏ dòng trùng.
- DISCOVERY: số test được chạy (0) và tên class/ file đã ghi.
- RUNTIME/ASSERTION: với mỗi method fail (tối đa 5): tên method, exception, thông điệp, 8 dòng đầu stack trace.
- REGRESSION: tên test sẵn có bị fail kèm exception và thông điệp (tối đa 5).

## 6. Flow 3.4–3.5: Can Repair Continue? và Repair Algorithm

### 6.1. Ai vào bước sửa

Candidate vào chain sửa khi **cả bốn** điều kiện đúng: manifest của dataset có `repair: true`; c0 có mã (không `INVALID_OUTPUT`); c0 không `INFRA`; c0 không `VALID`. Mỗi candidate như vậy chạy **mọi** cơ chế trong `repair.mechanisms`, mỗi cơ chế một chain độc lập, cùng c0, cùng N, cùng model và tham số.

**Tập con cố định của RQ2** *(chốt 15/10/2026, phương án E; Report 2 Experiment 2)*. Manifest đặt `repair_subset: K` (thực nghiệm chính: Classes2Test `K = 120`; GitBug-Java không đặt, tức sửa mọi ứng viên thất bại) thì chỉ K ứng viên thất bại được sửa cho RQ2:

1. **Pha 1** (`arrow run` trên mọi máy): khối 1, 2 và xác minh c0; chain của dataset có `repair_subset` **chờ** (sự kiện "chờ tập con RQ2"), khối 4 vẫn đo các bộ VALID ngay khi sinh.
2. **Chọn tập con** (người tổng hợp, sau khi gom run của mọi máy và chạy lại hết task INFRA): `arrow rq2-subset --experiment F --run-id A --run-id B …`. Ứng viên đủ điều kiện ở trên được chia theo ô model × quy trình, mỗi ô `K / số ô` ứng viên (2 model × 3 quy trình → 20); trong ô xếp theo `sha256("<seed>|<dataset>|<task_id>|<model>|<strategy>")` (seed 42) và lấy các ứng viên đầu. Thứ tự này chỉ phụ thuộc seed và định danh ứng viên nên tái lập được, không phụ thuộc máy hay thứ tự gộp. Ô có ít ứng viên hơn thì lấy hết, ghi `shortfall_by_cell` (Report 2: báo số thực tế và độ lệch). Lệnh từ chối khi còn task chưa chạy pha 1, có ở nhiều run, hoặc c0 còn INFRA. Kết quả ghi vào `repair_subset_file` của cấu hình (`experiments/<tên>.rq2-subset.json`, có `experiment_sha256`), commit lên Git.
3. **Pha 2** (mọi máy pull rồi chạy lại đúng lệnh `arrow run`): ứng viên trong tập con chạy mọi cơ chế (xen kẽ như AR Mục 8). Ứng viên **ngoài** tập con của quy trình `repair.rq3_strategy` (`zero_shot_project_aware`) chạy **chỉ Adaptive**, `final.json` có `rq3_only: true`, `run_order: "rq3_only"`: chain này chỉ để RQ3 có bộ test ARROW đại diện (Q3), **không** tính vào RQ2 hay quy tắc chọn N. Ứng viên ngoài tập con của các quy trình khác không được sửa. File tập con lệch `experiment_sha256` thì `run` từ chối (`RUN_CONFIG_MISMATCH`).

### 6.2. Can Repair Continue?

Sau mỗi vòng (và sau c0), chain dừng khi **một** điều kiện đúng:

| Điều kiện                                                                                               | `stop_reason` |
| ---------------------------------------------------------------------------------------------------------- | --------------- |
| Bản vừa xác minh `VALID`                                                                              | `valid`       |
| Đã chạy N vòng                                                                                         | `budget`      |
| Lỗi hạ tầng không tự khắc phục                                                                      | `infra`       |
| `early_stop_p` được bật và P vòng liên tiếp không có bản tốt hơn checkpoint (chỉ Adaptive) | `stagnation`  |

Trong thực nghiệm `early_stop_p = null` (không dừng sớm): chạy hết N vòng, P tính lại từ log (AR Mục 6.2). Fixed **không** dừng khi mã hay signature lặp lại.

### 6.3. So sánh hai bản (checkpoint)

Theo thứ tự, chỉ xét tiêu chí sau khi tiêu chí trước bằng nhau:

1. Bước xác minh đạt được, theo chuỗi `COMPILE < UNKNOWN < DISCOVERY < RUNTIME < ASSERTION < REGRESSION < VALID`;
2. `tests_passed` nhiều hơn;
3. `assertion_count` nhiều hơn;
4. Vòng xuất hiện sớm hơn.

Kết quả: `BETTER`, `WORSE`, hoặc `EQUAL` (bằng nhau ở cả ba tiêu chí đầu; khi đó bản cũ được giữ vì xuất hiện sớm hơn).

### 6.4. Adaptive Repair

**Trạng thái của thuật toán:** `best` (checkpoint, ban đầu = c0), `level` (1–3, ban đầu 1), `seen_hash` (ban đầu {hash(c0)}), `attempts` (danh sách "đã thử và thất bại", ban đầu rỗng).

```
for t = 1 … N:
    request ← BuildRequest(best, level, attempts)            # Mục 6.5
    ct      ← Khối2.RepairAsync(request)

    if ct không hợp lệ (INVALID_OUTPUT):
        attempts += Attempt(t, "invalid output: <lý do>") ; level ← NextLevel(level) ; continue
    if ct.code_hash ∈ seen_hash:
        attempts += Attempt(t, "duplicate code (same as round r)", change = Diff(best, ct))
        level ← NextLevel(level) ; continue
    seen_hash += ct.code_hash

    v ← Xácminh(ct)                                         # 3.1; INFRA → dừng chain
    if v.state = VALID: Finalize(success, ct) ; return

    cmp ← So sánh(v, best)
    if cmp = BETTER:
        if v.state ≠ best.state: level ← 1                   # lỗi đổi loại
        best ← ct (kèm v)                                    # cập nhật checkpoint
    else:
        attempts += Attempt(t, v.error_signature, first_error = FirstError(v), change = Diff(best, ct))
        if cmp = WORSE  or  v.error_signature = best.error_signature:
            level ← NextLevel(level)                        # rollback hoặc lỗi lặp
        # cmp = EQUAL và signature khác: giữ mức
        # vòng sau vẫn sửa từ best (rollback = không dùng ct)
Finalize(budget, best)
```

`NextLevel(level)`:

- `level < 3` → `level + 1`.
- `level = 3` → **2** *(chốt Q5, 14/10/2026)*: đã ở mức cao nhất mà vẫn không tiến triển thì quay lại mức 2 (thông tin bổ sung + danh sách đã thử), lần lỗi lặp tiếp theo lại lên 3. AR chỉ ghi `min(level + 1, 3)`; với quy tắc đó, khi checkpoint không đổi, prompt mức 3 giống hệt từng chữ ở mọi vòng, model (temperature 0) trả lại cùng câu trả lời và mọi vòng còn lại đều là mã trùng (pilot cũ: 5 chain kẹt như vậy tới hết N).

**Mức thực tế:** mức 3 chỉ dùng khi `best.state` ∈ {COMPILE, RUNTIME, ASSERTION} **và** `best.failing_methods` khác rỗng; nếu không, vòng đó dùng prompt mức 2 (`requested_level = 3`, `level = 2`).

**Danh sách "đã thử và thất bại" (`attempts`)** *(chốt 14/10/2026)*. Mỗi phần tử có hai loại thông tin cho hai mục đích:

| Trường        | Dùng cho                                                | Nội dung                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| --------------- | -------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `round`       | cả hai                                                  | Vòng của lần thử                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `signature`   | **thuật toán** (phát hiện lỗi lặp) và model | Error signature đã chuẩn hóa (Mục 5), hoặc `duplicate code (same as round r)`, hoặc `invalid output: <lý do>`                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| `first_error` | **model**                                          | Lỗi đầu tiên **chưa chuẩn hóa**, bỏ đường dẫn tuyệt đối, tối đa 200 ký tự: COMPILE: thông điệp javac kèm symbol và location (`cannot find symbol: method calcTotal(long) in class com.acme.shop.OrderService`); UNKNOWN: dòng `Failed to execute goal …` đầu tiên; DISCOVERY: `no tests executed`; RUNTIME: method, exception, thông điệp (`totalPriceAddsTax: NullPointerException at OrderService.totalPrice`); ASSERTION: method và thông điệp (`totalPriceAddsTax: expected:<110> but was:<100>`); REGRESSION: test sẵn có đầu tiên bị hỏng kèm exception. Không có với output không hợp lệ |
| `change`      | **model**                                          | Các dòng mã đã đổi so với checkpoint mà lần thử đó sửa từ (`-` dòng bị bỏ, `+` dòng thêm, không kèm dòng ngữ cảnh), tối đa 15 dòng, dư thì ghi `… (k more changed lines)`. Không có với output không hợp lệ                                                                                                                                                                                                                                                                                                                                                                                                            |

Khi điền vào prompt (`{{failed_attempts}}`): **3 lần thử gần nhất** hiện đủ `signature`, `first_error`, `change`; các lần cũ hơn chỉ hiện một dòng `round` + `signature` (để prompt không dài mãi). Thuật toán chỉ so `signature`; `first_error` và `change` chỉ để model biết **đã thử cái gì** mà tránh lặp, kể cả khi bản đó đã bị rollback và không còn trong mã gửi đi.

```
Approaches that already failed (do not repeat them):
- Round 1 · COMPILE | cannot find symbol | method
- Round 2 · COMPILE | cannot find symbol | method
  First error: cannot find symbol: method calcTotal(long) in class com.acme.shop.OrderService
  Changed code:
    -        long total = service.getTotal(1L);
    +        long total = service.calcTotal(1L);
- Round 3 · duplicate code (same as round 2)
  Changed code:
    -        long total = service.getTotal(1L);
    +        long total = service.calcTotal(1L);
```

### 6.5. Nội dung `RepairRequest` của Adaptive theo mức

Prompt luôn mô tả lỗi của **checkpoint** (`best`), vì mã gửi đi là mã của checkpoint.

| Mức | Template                  | `error_block` | `extra_context`                                               | `failed_attempts`                               | `rewrite_methods`      |
| ---- | ------------------------- | --------------- | --------------------------------------------------------------- | ------------------------------------------------- | ------------------------ |
| 1    | `repair/user/level1.md` | của best       | rỗng                                                           | rỗng                                             | rỗng                    |
| 2    | `repair/user/level2.md` | của best       | theo trạng thái (bảng dưới)                                | toàn bộ `attempts`                            | rỗng                    |
| 3    | `repair/user/level3.md` | của best       | **theo trạng thái, như mức 2** *(chốt 14/10/2026)* | toàn bộ `attempts` *(chốt Q5, 14/10/2026)* | `best.failing_methods` |

Mức 3 = **mức 2 + khoanh vùng method cần viết lại**: giữ nguyên thông tin bổ sung của mức 2 (ví dụ API thật của class liên quan tới lỗi), chỉ thu hẹp phạm vi sửa vào các method lỗi. Thu hẹp phạm vi không có nghĩa là bớt thông tin; với quy tắc xoay mức 2 ↔ 3 (Q5), bỏ thông tin ở mức 3 sẽ làm mất đúng thông tin vừa đưa ra ở mức 2.

Mọi mức đều có mã test của best, source của CUT (AR Mục 3.1, cập nhật 09/10) và **khối M đầy đủ** (spec 02 Mục 2.2; chốt Q4 = C, 14/10/2026). Vì Java version và dependencies đã nằm trong M ở mọi mức, thông tin bổ sung của mức 2 COMPILE chỉ còn phần M không có: API thật của các class liên quan tới lỗi.

**`extra_context` mức 2 theo trạng thái của best** (java-helper trích từ workspace, tối đa 200 dòng):

| Trạng thái | Nội dung                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| ------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| COMPILE      | Constructor, method và field công khai (chữ ký) của CUT và của **các class xuất hiện trong `symbol`/`location` của lỗi** (và trong thông điệp: `constructor A in class X`, `x has private access in X`, `X is not public in …`, `X is abstract; cannot be instantiated`; bổ sung 15/10/2026) (kể cả class của dependency, đọc từ jar đã phân giải); với lỗi `package does not exist`: danh sách package thật có trong các jar test của dự án (ví dụ `org.junit`, `org.mockito`) |
| UNKNOWN      | Toàn bộ khối lỗi build (tối đa 120 dòng) và cấu hình build trong M (build tool, plugin test, Java version)                                                                                                                                                                                                                                                                                                                                                                                                                           |
| DISCOVERY    | Khung test class mẫu đúng test framework của M: JUnit 4 (`import org.junit.Test`, class `public`), JUnit 5 (`import org.junit.jupiter.api.Test`), TestNG (`import org.testng.annotations.Test`); package và đường dẫn file theo test path                                                                                                                                                                                                                                                                                     |
| RUNTIME      | Mã nguồn của các method dự án xuất hiện trong stack trace; constructor và cách khởi tạo field của CUT; nếu dự án có Mockito: nhắc dùng mock cho dependency                                                                                                                                                                                                                                                                                                                                                                  |
| ASSERTION    | Giá trị expected/actual trích từ thông điệp; mã nguồn các method của CUT mà test method fail gọi tới                                                                                                                                                                                                                                                                                                                                                                                                                             |
| REGRESSION   | Tên và lỗi của test sẵn có bị fail; field `static` của CUT và các tài nguyên dùng chung (file, system property) mà test sinh ra chạm tới                                                                                                                                                                                                                                                                                                                                                                                     |

### 6.6. Fixed Repair (đối chứng)

```
latest ← c0
for t = 1 … N:
    request ← (template repair/user/level1.md, error_block của latest, mã của latest, source CUT, khối M đầy đủ)
    ct ← Khối2.RepairAsync(request)
    if ct không hợp lệ: continue                       # tính một vòng, latest giữ nguyên
    v ← Xácminh(ct)                                    # mã trùng vẫn build lại
    if v.state = VALID: Finalize(success, ct) ; return
    latest ← ct (kèm v)                                # luôn sửa tiếp từ bản mới nhất, kể cả khi tệ hơn
Finalize(budget, latest)
```

Fixed không dùng hash, signature, checkpoint, mức. Prompt Fixed bằng prompt mức 1 của Adaptive, nên vòng 1 của hai cơ chế có cùng prompt.

### 6.7. `decision.json` (mỗi vòng)

```json
{
  "round": 3, "mechanism": "adaptive", "base_round": 1,
  "requested_level": 3, "level": 2, "template": "repair/level2.md",
  "state": "COMPILE", "error_signature": "COMPILE | cannot find symbol | method",
  "code_hash": "sha256:…", "duplicate_code": false, "invalid_output": false,
  "comparison": "WORSE", "rollback": true, "repeated_signature": false, "improved": false,
  "level_after": 3, "checkpoint_round": 1,
  "tests_passed": 2, "assertion_count": 6, "dropped_methods": [],
  "llm_seconds": 4.2, "build_seconds": 18.5, "llm_tokens": { "in": 6120, "out": 1830 }, "llm_retries": 0, "replayed": false
}
```

## 7. Ví dụ (dùng làm unit test của thuật toán)

| Vòng | Kết quả xác minh                              | Adaptive quyết định                           | Fixed                             |
| ----- | ------------------------------------------------ | ------------------------------------------------ | --------------------------------- |
| c0    | COMPILE, sig A                                   | best = c0, level 1                               | latest = c0                       |
| 1     | RUNTIME                                          | BETTER, đổi trạng thái → best = r1, level 1 | latest = r1                       |
| 2     | COMPILE                                          | WORSE → rollback, attempts += r2, level 2       | latest = r2 (tệ hơn vẫn dùng) |
| 3     | mã trùng r2                                    | không build, attempts += r3, level 3            | build lại                        |
| 4     | ASSERTION                                        | BETTER → best = r4, level 1                     | latest = r4                       |
| 5     | ASSERTION, cùng sig với r4, ít test pass hơn | WORSE + lặp → level 2                          | latest = r5                       |
| 6     | VALID                                            | thành công ở vòng 6                          | …                                |

Thêm hai ví dụ cho Q5: (a) level 3, mã trùng → level 2, vòng sau prompt có thêm attempt mới nên request khác; (b) level 3 nhưng best ở DISCOVERY → vòng đó dùng mức 2.

## 8. Flow 3.6: Finalize Test Suite

`final.json` của mỗi chain:

```json
{
  "chain": { "task_id": "…", "model": "gpt-6-luna", "strategy": "zero_shot", "mechanism": "adaptive" },
  "c0_state": "COMPILE", "success": true, "success_round": 4, "rounds_run": 4, "stop_reason": "valid",
  "final_state": "VALID", "final_round": 4, "final_code": "final.java",
  "repair_seconds": 96.3, "llm_seconds": 21.0, "build_seconds": 75.3,
  "tokens": { "in": 24310, "out": 7010 }, "cost_usd": 0.0030,
  "counters": { "rollback": 1, "duplicate_code": 1, "invalid_output": 0, "repeated_signature": 1, "level_up": 3, "state_change": 2, "level3_rounds": 1 },
  "dropped_methods": [], "run_order": "first"
}
```

- `success_round` là chỉ số "number of repair attempts" của RQ2; chain không thành công tính bằng N.
- `repair_seconds` = tổng thời gian gọi model và build từ vòng 1 tới vòng thành công (hoặc hết N); lượt phát lại khi chạy tiếp tính theo thời gian của lần gọi thật (spec 02 Mục 4.4).
- `dropped_methods` = test method có trong c0 mà không còn trong bản cuối (do mức 3).
- Chỉ chain có `final_state = VALID` tạo bộ test cho khối 4 (`suite_id = <model>__<strategy>__<mechanism>`). Chain không VALID được ghi để báo cáo, không đo chất lượng.

## 9. Xác minh bộ test của hệ thống khác (EvoSuite)

Bộ test EvoSuite (spec 04 Mục 3) đi qua **đúng** Flow 3.1: bản sao mới của snapshot, cùng 5 bước, cùng lệnh; không qua chẩn đoán để sửa, không vào chain sửa. Khác biệt nằm hết trong `SuiteFiles` của bộ:

- `test_fqn = <CUT>_ESTest` (lệnh `-Dtest=<CUT>_ESTest`, Gradle `--tests <pkg>.<CUT>_ESTest`);
- `files` gồm test class và scaffolding;
- `extra_test_classpath` = `evosuite-standalone-runtime` (bộ test EvoSuite cần thư viện này để chạy): trong bản sao, Maven thêm dependency scope `system` với `systemPath` trỏ tới file jar vào `pom.xml` của module đích (chỉ trong bản sao; Maven không có cách khác trỏ dependency tới một file jar mà không cài vào `~/.m2`, scope `system` cũng có trên classpath test); Gradle thêm `testImplementation files(…)` qua một init script phụ. Đây là ngoại lệ đã khai báo, giống nhau cho mọi lần xác minh và đo bộ EvoSuite.

Kết quả `block3/evosuite/verify.json`; chỉ bộ VALID được đo ở khối 4.

## 10. Mã lỗi

| Mã               | Kết quả | Khi nào                                                           |
| ----------------- | --------- | ------------------------------------------------------------------ |
| `B3_TIMEOUT`    | INFRA     | Bước build/test quá `step_timeout_seconds` sau khi chạy lại |
| `B3_DEPENDENCY` | INFRA     | Lỗi tải dependency                                               |
| `B3_TOOL_ERROR` | INFRA     | Maven/Gradle/JVM không chạy được                              |
| `B3_WORKSPACE`  | INFRA     | Không tạo được workspace của job (đĩa, quyền)             |

## 11. Interface C#

```csharp
public interface IVerifier
{
    // Flow 3.1; dùng chung cho candidate ARROW (SuiteFiles từ CandidateTest) và bộ EvoSuite.
    Task<VerifyResult> VerifyAsync(SuiteFiles suite, TaskContext task, VerifyOptions options, CancellationToken ct);
}

public interface IFailureDiagnoser
{
    Diagnosis Diagnose(VerifyResult result, CandidateTest candidate, RepositoryMetadata m);                         // Flow 3.3, code thuần
}

public interface IRepairPolicy                       // AdaptiveRepairPolicy, FixedRepairPolicy: code thuần
{
    RepairRequest NextRequest();                     // Flow 3.5 (Mục 6.4–6.6)
    RoundDecision Apply(RoundOutcome outcome);       // cập nhật checkpoint, mức, attempts
    bool CanContinue(int round, int n);              // Flow 3.4
}

public interface IRepairRunner
{
    Task<FinalTestSuite> RunChainAsync(CandidateTest c0, VerifyResult c0Result, string mechanism, TaskContext task, CancellationToken ct);
}
```

## 12. Kiểm thử

- Unit: lệnh bước 2 lấy tên từ `SuiteFiles.test_fqn` (`…_ArrowTest`, `…_ESTest`); biểu thức lỗi javac với đường dẫn macOS, `/D:/…` và `D:\…`; phân loại DISCOVERY/RUNTIME/ASSERTION từ XML mẫu (kể cả `type` chứa thông điệp); signature từng trạng thái; so sánh checkpoint; bảng ví dụ Mục 7 cho cả Adaptive và Fixed; ví dụ Q5.
- Integration: 3 fixture với candidate cố ý sai: lỗi biên dịch, không có `@Test`, NPE, sai expected, làm hỏng test sẵn có (biến static), và một candidate đúng → đủ 6 trạng thái.

## 13. Chờ xác nhận

Giải thích chi tiết (bối cảnh, ảnh hưởng tới RQ, các phương án, đề xuất) ở **spec 05**.

| #  | Điểm                                            | Đề xuất trong spec                                                                                        |
| -- | ------------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| Q4 | Prompt sửa có kèm metadata M không            | **Chốt C:** M đầy đủ cho mọi prompt sửa, cả Fixed và Adaptive (Mục 6.5–6.6)                 |
| Q5 | Kẹt ở mức 3 khi mã trùng                     | **Chốt B:** `NextLevel(3) = 2`, prompt mức 3 có danh sách đã thử (Mục 6.4–6.5)              |
| Q6 | Quy tắc chọn N                                  | **Chốt A:** quy tắc của CLAUDE.md, chọn bằng pilot sau khi code xong (spec 04 Mục 6.3)           |
| Q7 | UNKNOWN có mức 2 riêng không (AR không nói) | **Chốt B:** khối lỗi build đầy đủ và cấu hình build (Mục 6.5)                               |
| Q8 | Timeout                                           | **Chốt:** thử tối đa 3 lần; vẫn timeout → INFRA, ghi rõ cần chạy lại riêng task (Mục 3.5) |

## 14. Một file test đi qua khối 3 như thế nào (giải thích dễ hiểu)

Mục này kể lại Mục 3–8 theo góc nhìn của **một file test**, với CUT minh họa `com.acme.shop.OrderService` và file test `OrderService_ArrowTest.java` do khối 2 sinh ra (c0).

### 14.1. Bức tranh chung

```
                         OrderService_ArrowTest.java (c0, từ khối 2)
                                        │
          (0) Khối 2 có tách được mã Java hợp lệ không? ── Không ──► INVALID_OUTPUT: ghi kết quả, KHÔNG sửa (hết)
                                        │ Có
          (1) Biên dịch (test-compile) ─── lỗi ──► COMPILE (có lỗi javac trong file test) / UNKNOWN (build hỏng, không có lỗi javac)
                                        │ được
          (2) JUnit có tìm thấy test? ──── không ─► DISCOVERY
                                        │ có
          (3) Chạy: có test văng exception (không phải assertion)? ── có ──► RUNTIME
                                        │ không
          (4) Mọi assertion đều đúng? ──── không ─► ASSERTION
                                        │ đúng
          (5) Chạy cả module: test cũ còn pass, test mới vẫn pass? ── không ──► REGRESSION
                                        │ có
                                      VALID ───────────────────────────────────────────► khối 4 (đo chất lượng)

   Bất kỳ bước nào: timeout, không tải được dependency, Maven/Gradle/JVM chết
        → chạy lại bước đó, tối đa 3 lần thử → vẫn lỗi → INFRA + ghi "cần chạy lại riêng task này"
```

Hỏng ở bước nào thì test nhận **trạng thái** của bước đó. Trạng thái xếp theo "đi được bao xa":
`COMPILE < UNKNOWN < DISCOVERY < RUNTIME < ASSERTION < REGRESSION < VALID`. Bản đi xa hơn là bản tốt hơn.

### 14.2. Trường hợp thành công: TEST → build và chạy → VALID → khối 4

```
OrderService_ArrowTest (c0) → (1) biên dịch được → (2) JUnit thấy 5 test → (3)(4) 5/5 pass
→ (5) chạy cả module shop-core: 41 test cũ vẫn pass, 5 test mới pass → VALID
```

- Ghi `verify_c0.json` với `state = VALID`.
- **Không** chạy chain sửa.
- Tạo bộ test `gpt-6-luna__zero_shot_project_aware__initial` đưa sang khối 4.
- Ở RQ1, candidate này được tính "qua cả 5 bước".

### 14.3. Trường hợp thất bại: có những lỗi nào, framework nhận ra thế nào, xử lý ra sao

Mỗi dòng là một trường hợp có thể xảy ra với c0. Cột "Sửa" cho biết c0 có vào chain sửa không. Cột Adaptive mô tả **mức 1 → mức 2 → mức 3** dùng cho trạng thái đó (Mục 6.5); Fixed luôn dùng một prompt (lỗi + mã + source CUT).

| #   | Hỏng ở đâu                                                                                    | Ví dụ dấu hiệu                                                                                                                                                                                | Trạng thái           | Signature ví dụ                                                                     | Sửa                                                                                                                                                                      | Adaptive xử lý theo mức                                                                                                                                                                                                                                                                                                                                                    | Fixed                   |
| --- | ------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------- | ------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------- |
| F0  | Model không trả mã dùng được                                                               | Chỉ có chữ giải thích; hai class; mã bị cắt                                                                                                                                               | `INVALID_OUTPUT`     | –                                                                                    | **Không** (không có mã để sửa)                                                                                                                               | –                                                                                                                                                                                                                                                                                                                                                                            | –                      |
| F1  | Biên dịch: gọi thứ không tồn tại                                                           | `OrderService_ArrowTest.java:[14,30] cannot find symbol` `symbol: method getTotal(long)`                                                                                                      | COMPILE                | `COMPILE \| cannot find symbol \| method`                                             | Có                                                                                                                                                                       | M1: lỗi javac + mã test + source CUT. M2: thêm **API thật** của `OrderService` và các class trong lỗi (constructor, method, field) và danh sách đã thử (Java version, dependencies đã có trong khối M ở mọi mức). M3: thông tin như M2, nhưng chỉ viết lại **method chứa dòng lỗi** (`totalPriceAddsTax`), ghép vào checkpoint | Cùng prompt mọi vòng |
| F2  | Biên dịch: sai kiểu, sai tham số                                                              | `incompatible types: String cannot be converted to long`; `constructor OrderService in class OrderService cannot be applied to given types`                                                   | COMPILE                | `COMPILE \| incompatible types \| -`; `COMPILE \| constructor cannot be applied \| -` | Có                                                                                                                                                                       | Như F1                                                                                                                                                                                                                                                                                                                                                                       | Như F1                 |
| F3  | Biên dịch: thư viện không có trong dự án                                                  | `package org.junit.jupiter.api does not exist` (dùng JUnit 5 trong dự án JUnit 4); `package org.assertj.core.api does not exist`                                                           | COMPILE                | `COMPILE \| package does not exist \| -`                                              | Có                                                                                                                                                                       | Như F1; khối M (có ở mọi mức) cho model thấy dự án chỉ có JUnit 4 + Mockito; M2 thêm danh sách package thật có trong các jar test (`org.junit`, `org.mockito`)                                                                                                                                                                                            | Như F1                 |
| F4  | Biên dịch: lỗi javac khác                                                                     | `unreported exception IOException`; `has private access`; `is abstract; cannot be instantiated`                                                                                             | COMPILE                | `COMPILE \| unreported exception \| -` …                                             | Có                                                                                                                                                                       | Như F1                                                                                                                                                                                                                                                                                                                                                                       | Như F1                 |
| F5  | Build hỏng nhưng không có lỗi javac trong file test                                          | `Failed to execute goal …maven-enforcer-plugin…`; annotation processor lỗi; task Gradle lỗi                                                                                                 | UNKNOWN                | `UNKNOWN \| maven-enforcer-plugin \| -`                                               | Có                                                                                                                                                                       | M1: khối lỗi build (60 dòng). M2: khối lỗi build đầy đủ (120 dòng) + cấu hình build (Q7). M3: không áp dụng → dùng M2                                                                                                                                                                                                                                        | Như F1                 |
| F6  | Biên dịch được nhưng JUnit không chạy test nào                                           | Thiếu `@Test`; dùng `@org.junit.jupiter.api.Test` trong dự án chỉ có JUnit 4; JUnit 4 báo `No runnable methods`                                                                      | DISCOVERY              | `DISCOVERY \| no tests executed \| -`                                                 | Có                                                                                                                                                                       | M1: báo "0 test được chạy" + mã. M2: **khung test class mẫu** đúng JUnit 4 (import `org.junit.Test`, class `public`), đúng package và đường dẫn file. M3: không áp dụng → M2                                                                                                                                                                    | Như F1                 |
| F7  | Chạy: test văng exception                                                                       | `NullPointerException at OrderService.totalPrice(OrderService.java:31)`; Mockito `UnfinishedStubbingException`; `IllegalArgumentException` từ CUT                                          | RUNTIME                | `RUNTIME \| NullPointerException \| OrderService.totalPrice`                          | Có                                                                                                                                                                       | M1: method lỗi + exception + 8 dòng stack trace. M2: **mã nguồn method** trong stack trace (`totalPrice`), constructor và cách khởi tạo field của `OrderService`, gợi ý mock. M3: thông tin như M2, nhưng chỉ viết lại **các test method bị lỗi**                                                                                         | Như F1                 |
| F9  | Chạy xong nhưng kết quả sai                                                                   | `expected:<110> but was:<100>`; `Expected IllegalStateException to be thrown, but nothing was thrown`                                                                                         | ASSERTION              | `ASSERTION \| AssertionError \| totalPriceAddsTax`                                    | Có                                                                                                                                                                       | M1: method fail + thông điệp expected/actual. M2: giá trị expected/actual, **mã các method của CUT** mà test gọi. M3: thông tin như M2, nhưng chỉ viết lại các test method fail. Model được sửa tự do, kể cả đổi expected (CLAUDE.md hạng mục 9)                                                                                            | Như F1                 |
| F10 | Test mới pass khi chạy riêng nhưng làm hỏng module                                          | `ClockTest`, `InventoryTest` pass ở baseline nhưng fail khi chạy cùng test mới (test mới đổi biến `static`, ghi file, đổi system property); hoặc test mới fail khi chạy chung | REGRESSION             | `REGRESSION \| existing tests fail \| ClockTest,InventoryTest`                        | Có                                                                                                                                                                       | M1: tên và lỗi của test cũ bị hỏng. M2: thêm field `static` và tài nguyên dùng chung mà test mới chạm tới. M3: không áp dụng → M2                                                                                                                                                                                                                       | Như F1                 |
| I1  | Hạ tầng: quá thời gian ở bất kỳ bước nào (biên dịch, chạy riêng test, chạy module) | Hết `step_timeout_seconds` ở cả **3 lần thử**                                                                                                                                        | INFRA, `needs_rerun` | –                                                                                    | **Chưa**: vào danh sách "Cần chạy lại riêng" kèm lệnh `arrow run --tasks <task_id> --rerun-infra`; chưa tính vào RQ1/RQ2 cho tới khi chạy lại xong | –                                                                                                                                                                                                                                                                                                                                                                            | –                      |
| I2  | Hạ tầng: không tải được dependency                                                         | `Could not resolve dependencies`, mạng lỗi ở cả 3 lần thử                                                                                                                                 | INFRA, `needs_rerun` | –                                                                                    | Như I1                                                                                                                                                                   | –                                                                                                                                                                                                                                                                                                                                                                            | –                      |
| I3  | Hạ tầng: công cụ hỏng                                                                        | Maven/JVM bị giết, hết đĩa, không tạo được workspace (3 lần thử)                                                                                                                      | INFRA, `needs_rerun` | –                                                                                    | Như I1                                                                                                                                                                   | –                                                                                                                                                                                                                                                                                                                                                                            | –                      |
| G   | Dataset không bật sửa                                                                          | GitBug-Java ở pilot (`repair: false`)                                                                                                                                                          | trạng thái của c0   |                                                                                       | **Không**: chỉ ghi kết quả xác minh cho RQ1                                                                                                                    | –                                                                                                                                                                                                                                                                                                                                                                            | –                      |

Ghi chú:

- Một file test có thể có nhiều lỗi cùng lúc. Trạng thái lấy theo **bước hỏng đầu tiên** (ví dụ vừa lỗi biên dịch vừa sai assertion thì là COMPILE, vì chưa chạy được). Signature lấy theo lỗi đầu tiên, ưu tiên lỗi nằm trong file test.
- Lỗi COMPILE do đường dẫn Windows (`/D:/…`, `D:\…`) vẫn được nhận đúng là COMPILE (Mục 3.2), không bị nhầm thành UNKNOWN như tool cũ.

### 14.4. Sau mỗi vòng sửa: bản mới gặp những trường hợp nào

Chain sửa gửi `RepairRequest` sang khối 2, nhận bản mới `ct`, xác minh lại bằng 14.1, rồi so `ct` với **checkpoint** (Adaptive) hoặc chỉ nhận `ct` làm bản mới nhất (Fixed).

| #   | Bản mới `ct`                                                                               | Adaptive                                                                                                                                                         | Fixed                                                                    |
| --- | ---------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------ |
| O1  | **VALID**                                                                                | Dừng, thành công ở vòng này → bộ test sang khối 4                                                                                                       | Như Adaptive                                                            |
| O2  | Output không hợp lệ (không có mã)                                                        | Tính 1 vòng, không build; ghi "invalid output" vào đã thử; **lên mức**                                                                            | Tính 1 vòng; bản mới nhất giữ nguyên                              |
| O3  | **Mã trùng** một bản đã thử (cùng hash)                                          | Tính 1 vòng, **không build**; ghi "duplicate code"; **lên mức** (đang ở mức 3 thì về mức 2, Q5)                                           | **Vẫn build lại** như bản mới (Fixed không kiểm tra trùng) |
| O4  | **Tốt hơn** checkpoint và **khác trạng thái** (ví dụ COMPILE → ASSERTION) | `ct` thành checkpoint; **về mức 1** với prompt của trạng thái mới                                                                                | `ct` thành bản mới nhất                                            |
| O5  | **Tốt hơn**, cùng trạng thái (ví dụ ASSERTION 2/5 → 4/5 test pass)               | `ct` thành checkpoint; **giữ mức**                                                                                                                    | `ct` thành bản mới nhất                                            |
| O6  | **Tệ hơn** checkpoint (ví dụ ASSERTION → COMPILE)                                   | **Rollback**: bỏ `ct`, vòng sau sửa từ checkpoint; ghi signature của `ct` vào đã thử; **lên mức**                                     | **Không rollback**: vòng sau sửa từ `ct` dù tệ hơn        |
| O7  | **Ngang** checkpoint, **cùng signature** (lỗi lặp lại)                         | Bỏ `ct`; ghi vào đã thử; **lên mức**                                                                                                              | Sửa tiếp từ `ct`                                                    |
| O8  | **Ngang** checkpoint, **khác signature** (lỗi khác, cùng mức độ)            | Bỏ `ct`; ghi vào đã thử; **giữ mức**                                                                                                              | Sửa tiếp từ `ct`                                                    |
| O9  | INFRA khi xác minh `ct` (sau 3 lần thử)                                                   | Chain dừng `INFRA`, vào danh sách "Cần chạy lại riêng"; chạy lại **từ đầu chain** (lượt gọi model đã có trong cache không tốn tiền) | Như Adaptive                                                            |
| O10 | Đã chạy đủ N vòng mà chưa VALID                                                        | Kết thúc: `success = false`, bộ test cuối = checkpoint; **không** đo ở khối 4                                                                    | Kết thúc: bộ test cuối = bản mới nhất; không đo                 |

"Tốt hơn / tệ hơn / ngang" so theo Mục 6.3: bước xác minh đạt được → số test pass → số assertion.

### 14.5. Ví dụ trọn vẹn: một file test từ c0 tới khối 4

c0 = `OrderService_ArrowTest` (quy trình `zero_shot`), N = 5.

**Xác minh c0:** biên dịch lỗi `cannot find symbol: method getTotal(long)` → **COMPILE** (F1). Có mã, dataset bật sửa → vào **hai** chain: Fixed và Adaptive, cùng c0.

**Chain Adaptive:**

| Vòng | Prompt gửi đi (sửa từ)                                                                                                                                              | Kết quả `ct`                                                                               | Quyết định                                                         | Mức sau |
| ----- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- | --------------------------------------------------------------------- | -------- |
| 1     | M1 COMPILE (c0): lỗi `getTotal` + mã + source CUT                                                                                                                   | COMPILE, lỗi khác: `constructor OrderService cannot be applied`                            | O8: ngang, khác signature → giữ mức; đã thử += vòng 1         | 1        |
| 2     | M1 COMPILE (c0) — prompt **giống hệt vòng 1** (lượt gọi mới, nhưng temperature 0) → model trả lại đúng bản vòng 1                                 | Mã trùng vòng 1                                                                             | O3: không build, lên mức                                           | 2        |
| 3     | M2 COMPILE (c0): + API thật của `OrderService` (`totalPrice(long)`, constructor 2 tham số) + đã thử (vòng 1, 2, kèm lỗi cụ thể và dòng mã đã đổi) | Biên dịch được; `totalPriceAddsTax` fail: `expected:<110> but was:<100>` → ASSERTION | O4: tốt hơn, đổi trạng thái → checkpoint = vòng 3, về mức 1 | 1        |
| 4     | M1 ASSERTION (vòng 3): method fail + expected/actual                                                                                                                   | ASSERTION, cùng signature, 4/5 pass như vòng 3                                              | O7: lỗi lặp → lên mức; đã thử += vòng 4                      | 2        |
| 5     | M2 ASSERTION (vòng 3): + mã `totalPrice`, `TaxPolicy.apply` + đã thử                                                                                           | 5/5 pass; chạy cả module: 41 test cũ pass → **VALID**                                | O1: thành công ở vòng 5                                           | –       |

→ `final.json`: `success = true`, `success_round = 5`; bộ test `gpt-6-luna__zero_shot__adaptive` sang khối 4.

**Chain Fixed (cùng c0):**

| Vòng | Prompt (sửa từ bản mới nhất)                        | Kết quả                                             |
| ----- | -------------------------------------------------------- | ----------------------------------------------------- |
| 1     | Prompt Fixed (c0) — giống prompt vòng 1 của Adaptive | COMPILE (constructor)                                 |
| 2     | Prompt Fixed (vòng 1)                                   | ASSERTION 4/5                                         |
| 3     | Prompt Fixed (vòng 2)                                   | COMPILE (tệ hơn, nhưng Fixed vẫn dùng bản này) |
| 4     | Prompt Fixed (vòng 3)                                   | ASSERTION 4/5                                         |
| 5     | Prompt Fixed (vòng 4)                                   | ASSERTION 4/5                                         |

→ `final.json`: `success = false`, `success_round = 5` (không thành công tính bằng N), bộ test cuối = vòng 5; **không** đo ở khối 4. RQ2 ghi nhận chain này.

### 14.6. Tóm tắt: cái gì đi sang khối 4

| Kết quả cuối                | Sang khối 4?                                 | Ghi vào đâu                                                             |
| ------------------------------ | --------------------------------------------- | -------------------------------------------------------------------------- |
| c0 VALID                       | Có, bộ `…__initial`                      | RQ1 (qua 5 bước)                                                         |
| Chain đạt VALID              | Có, bộ `…__fixed` hoặc `…__adaptive` | RQ2 (thành công)                                                         |
| Chain hết N vòng chưa VALID | Không                                        | RQ2 (thất bại)                                                           |
| c0 INVALID_OUTPUT              | Không                                        | RQ1 (không qua bước nào)                                               |
| INFRA                          | Không (chờ chạy lại)                      | Danh sách "Cần chạy lại riêng" trong báo cáo, kèm lệnh chạy lại |
| Bộ EvoSuite VALID (Mục 9)    | Có, bộ `evosuite`                         | RQ3                                                                        |

## 15. REGRESSION và rollback: khi nào, ví dụ

Hai từ này hay bị nhầm vì `CLAUDE.md` viết "Rollback: khôi phục phiên bản tốt hơn khi lần sửa mới **gây regression**". Trong framework chúng là hai thứ khác nhau:

|                                   | REGRESSION                                                   | Rollback                                                                                           |
| --------------------------------- | ------------------------------------------------------------ | -------------------------------------------------------------------------------------------------- |
| Là gì                           | Một **trạng thái xác minh** (bước 5)             | Một **quyết định** của Adaptive Repair                                                  |
| Áp dụng cho                     | Mọi bộ test: c0, mọi vòng sửa, cả Fixed, cả EvoSuite  | Chỉ chain **Adaptive**, từ vòng 1 trở đi                                                |
| Câu hỏi nó trả lời           | "Test mới có làm hỏng test sẵn có của module không?" | "Bản vừa sửa có **đi lùi** so với bản tốt nhất không?"                            |
| Chữ "regression" trong CLAUDE.md | –                                                           | Nghĩa là **đi lùi** (bản mới tệ hơn), **không** phải trạng thái REGRESSION |

### 15.1. Khi nào là REGRESSION

Điều kiện: test sinh ra **đã qua bước 1–4** (biên dịch được, JUnit thấy test, chạy không văng exception, mọi assertion đúng khi chạy **riêng**), nhưng ở bước 5, khi chạy **cả module** (test sẵn có + test sinh ra):

- (a) một test có trong `baseline.passing_in_all_runs` bị fail, **hoặc**
- (b) một test đó **biến mất** khỏi báo cáo (không được chạy), **hoặc**
- (c) chính test sinh ra fail khi chạy chung (dù pass khi chạy riêng).

| #  | Ví dụ                                                                                                                                                                                 | Vì sao là REGRESSION                                   | Signature                                                      |
| -- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------- | -------------------------------------------------------------- |
| R1 | Test mới gọi `TaxPolicy.setDefaultRate(20)` (biến `static`) và không đặt lại. `InvoiceTest#appliesDefaultTax` (pass ở baseline, cần thuế 10%) chạy sau đó thì fail | (a): test sẵn có bị hỏng do trạng thái dùng chung | `REGRESSION \| existing tests fail \| InvoiceTest`             |
| R2 | Test mới xóa file `src/test/resources/orders.json`; `OrderRepositoryTest` đọc file đó thì fail                                                                               | (a): tài nguyên dùng chung                            | `REGRESSION \| existing tests fail \| OrderRepositoryTest`     |
| R3 | Test mới gọi `Locale.setDefault(Locale.FRANCE)` hoặc `TimeZone.setDefault(...)`; test định dạng số/ngày sẵn có fail                                                       | (a): cấu hình JVM dùng chung                          | `REGRESSION \| existing tests fail \| PriceFormatTest`         |
| R4 | Test mới gọi `System.exit(0)` (qua code CUT); JVM chạy test dừng giữa chừng, 20 test sẵn có không được chạy                                                              | (b): test sẵn có biến mất                            | `REGRESSION \| existing tests fail \| CartTest,InvoiceTest,…` |
| R5 | Test mới pass khi chạy riêng, nhưng fail khi chạy chung vì `CartTest` chạy trước đã đổi một singleton                                                                   | (c): test mới phụ thuộc thứ tự                      | `REGRESSION \| existing tests fail \| OrderService_ArrowTest`  |

**Không phải REGRESSION:**

| Tình huống                                                                                     | Kết quả                                                       |
| ------------------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| Test sẵn có `ClockTest#now` fail, nhưng ở baseline nó là `flaky` (lúc pass lúc fail) | Bỏ qua, không kết luận regression                           |
| Test sẵn có fail ở **cả hai** lần baseline (`failing_in_all_runs`)                  | Bỏ qua: nó vốn đã hỏng                                    |
| Test mới fail ngay khi chạy **riêng**                                                   | Không tới bước 5: là RUNTIME hoặc ASSERTION (bước 3–4) |
| Test mới không biên dịch được                                                             | COMPILE (bước 1)                                              |

**REGRESSION xếp ở đâu trong chuỗi:** `… < ASSERTION < REGRESSION < VALID`. Một bản REGRESSION đã làm được nhiều hơn bản ASSERTION (mọi test mục tiêu đều pass), chỉ còn thiếu bước cuối, nên được coi là **tốt hơn** ASSERTION khi so checkpoint. Nhóm lỗi để báo cáo: REGRESSION thuộc nhóm RUNTIME.

**Adaptive sửa REGRESSION thế nào:** mức 1: tên và lỗi của test sẵn có bị hỏng. Mức 2: thêm field `static`, file, system property mà test mới chạm tới (ví dụ R1: "test mới gọi `TaxPolicy.setDefaultRate`; `InvoiceTest` cần giá trị mặc định 10"). Mức 3 không áp dụng (dùng mức 2). Cách sửa điển hình mà model làm: đặt lại giá trị trong `@After`, dùng thư mục tạm thay vì file thật, mock thay vì đổi singleton.

### 15.2. Khi nào rollback

Rollback xảy ra **chỉ trong chain Adaptive**, khi bản vừa sửa `ct` **được build** và **tệ hơn checkpoint** theo thứ tự so sánh (Mục 6.3):

1. đi được ít bước xác minh hơn, hoặc
2. cùng bước nhưng ít test method pass hơn, hoặc
3. cùng bước, cùng số test pass, nhưng ít assertion hơn.

Khi rollback: **bỏ `ct`**, vòng sau sửa từ checkpoint (không từ `ct`); ghi `ct` vào danh sách đã thử ("round t: COMPILE | …"); **lên mức** (đang ở mức 3 thì về mức 2, Q5); `decision.json` ghi `rollback: true`; bộ đếm `rollback` của chain tăng 1.

| #  | Checkpoint                       | Bản mới `ct`                                                       | So sánh                                          | Rollback?     | Vòng sau                              |
| -- | -------------------------------- | ---------------------------------------------------------------------- | ------------------------------------------------- | ------------- | -------------------------------------- |
| B1 | ASSERTION, 4/5 test pass         | COMPILE (sửa assertion nhưng làm sai cú pháp)                     | `ct` đi ít bước hơn                        | **Có** | Sửa từ checkpoint ASSERTION; mức +1 |
| B2 | ASSERTION, 4/5 pass              | ASSERTION, 2/5 pass                                                    | Cùng bước, ít test pass hơn                  | **Có** | Sửa từ checkpoint 4/5                |
| B3 | ASSERTION, 4/5 pass, 9 assertion | ASSERTION, 4/5 pass, 6 assertion (model xóa bớt assertion)           | Cùng bước, cùng test pass, ít assertion hơn | **Có** | Sửa từ checkpoint 9 assertion        |
| B4 | REGRESSION (R1 ở trên)         | ASSERTION (model sửa phần đặt lại thuế nhưng làm sai expected) | ASSERTION đứng trước REGRESSION               | **Có** | Sửa từ checkpoint REGRESSION         |
| B5 | RUNTIME                          | DISCOVERY (model xóa mất `@Test`)                                  | DISCOVERY đứng trước RUNTIME                  | **Có** | Sửa từ checkpoint RUNTIME            |

**Không phải rollback:**

| #  | Checkpoint                              | Bản mới `ct`                          | Kết quả                                                                                                                                         |
| -- | --------------------------------------- | ----------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| N1 | ASSERTION 4/5                           | REGRESSION                                | **Tốt hơn**: `ct` thành checkpoint; đổi loại lỗi → mức 1 (prompt REGRESSION)                                                     |
| N2 | ASSERTION 3/5                           | ASSERTION 4/5                             | **Tốt hơn**, cùng loại → checkpoint mới, giữ mức                                                                                    |
| N3 | ASSERTION 4/5, 9 assertion, signature A | ASSERTION 4/5, 9 assertion, signature B   | **Ngang**: không rollback (không tệ hơn); `ct` không được dùng, vòng sau vẫn sửa từ checkpoint; khác signature → giữ mức |
| N4 | ASSERTION 4/5, signature A              | ASSERTION 4/5, signature A                | **Ngang và lặp lỗi**: không gọi là rollback, nhưng lên mức (lỗi lặp)                                                             |
| N5 | bất kỳ                                | Mã trùng / output không hợp lệ       | Không build nên không so sánh, không rollback; lên mức                                                                                     |
| N6 | –                                      | Chain **Fixed**, bản mới tệ hơn | Fixed **không bao giờ rollback**: vòng sau sửa tiếp từ bản tệ hơn đó                                                             |

Ghi chú: trường hợp "ngang" (N3, N4) cũng không dùng `ct` (vòng sau vẫn sửa từ checkpoint, vì bản cũ xuất hiện sớm hơn), nhưng **không** đếm là rollback; chỉ trường hợp tệ hơn mới đếm. Báo cáo RQ2 thống kê số lần rollback theo đúng định nghĩa này.

### 15.3. Một chain có cả hai

c0 = ASSERTION (4/5 pass).

| Vòng | `ct`                                                                                                                                                                                                                      | Quyết định Adaptive                                                                       | Checkpoint sau vòng |
| ----- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------- | -------------------- |
| 1     | REGRESSION (R1: đổi thuế `static`, `InvoiceTest` hỏng)                                                                                                                                                              | Tốt hơn (REGRESSION > ASSERTION) → checkpoint mới; đổi loại lỗi → mức 1 REGRESSION | vòng 1 (REGRESSION) |
| 2     | ASSERTION (model thêm `@After` đặt lại thuế nhưng viết sai expected)                                                                                                                                               | **Tệ hơn** → **rollback**; đã thử += vòng 2; mức 2                       | vẫn vòng 1         |
| 3     | Mức 2 REGRESSION (thêm: "test gọi `TaxPolicy.setDefaultRate`; `InvoiceTest` cần mặc định 10") → model thêm `@After` đặt lại đúng, giữ expected cũ → module không còn test hỏng → **VALID** | Thành công ở vòng 3                                                                      | –                   |

Fixed với cùng c0: vòng 2 tệ hơn vẫn được dùng làm bản sửa tiếp ở vòng 3 (không quay về bản REGRESSION của vòng 1).
