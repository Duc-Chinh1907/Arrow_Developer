# 01. Khối 1: Repository-Aware

**Mục đích:** từ một CUT trong manifest, dựng lại repository đúng commit, xác định đúng thư mục build, phân tích 6 thông tin cấu hình (Đóng góp 1) và xuất **Repository Metadata (M)**. Khối này cũng chạy **baseline** (test sẵn có của module) để khối 3 kiểm tra module regression, và kiểm tra **tiêu chí B** (test người viết ghép với CUT chạy và pass ổn định) để sàng lọc mẫu.

```
Input Repository ──► Xác định build root, module root ──► Repository Analysis (6 thông tin) ──► Configuration Output: M
        │                                                                                              │
        └──────────────► Baseline (biên dịch → snapshot → chạy test sẵn có ×2, tiêu chí B) ───────────┘
```

## 1. Input

### 1.1. Bản ghi manifest (`TaskRecord`)

Một dòng của file `.jsonl` (bản ghi preflight giữ nguyên). Khối 1 dùng các trường sau; các trường khác được chép nguyên vào `block1/task.json` để tra cứu. Ví dụ lấy từ manifest pilot thật.

| Trường | Kiểu | Ví dụ thật | Dùng để |
| --- | --- | --- | --- |
| `task_id` | string | `118477931_429246acc7725217` | Định danh task |
| `repo_url` | string | `https://github.com/…` | Tải repository |
| `checkout_sha` | string (40 hex) | `ff3ffca2…` | Commit cần dựng lại |
| `class_path` | string | `3rdParty/libsodium-jni-2.0.1/src/main/java/org/libsodium/jni/crypto/Util.java` | File CUT, **tính từ gốc repository** |
| `class_fqn` | string | `org.libsodium.jni.crypto.Util` | Tên đầy đủ của CUT |
| `module_path` | string | `.`, `jpa2ddl-core`, `integrating/spring` | Module chứa CUT, tính từ thư mục mà preflight chạy lệnh reactor đầu tiên. **Không** dùng một mình để dựng lệnh build: build root và bộ chọn module lấy từ lần biên dịch thành công (Mục 4.1) |
| `working_directory` | string | `…/preflight_tool/runs/…/workspaces/118477931_…` | Gốc repository trong workspace của preflight (chỉ dùng để tính đường dẫn tương đối) |
| `build_attempts[]` | list | `{ stage: "test_compile_fallback", command: ["mvn","-q","-DskipTests","test-compile"], working_directory: "…/integrating/spring", exit_code: 0 }` | Các lệnh preflight đã chạy. **Lần biên dịch test thành công** cho biết build root và bộ chọn module thật sự dùng được (Mục 4.1) |
| `test_class_path` | string \| null | `3rdParty/libsodium-jni-2.0.1/src/test/java/org/libsodium/jni/crypto/UtilTest.java` | Test người viết ghép với CUT: dùng cho tiêu chí B (Mục 6.3); **không** gửi cho LLM |
| `build_tool` | `maven` \| `gradle` | `maven` | Gợi ý; khối 1 tự dò lại |
| `java_version` | string | `openjdk version "1.8.0_504"`, `openjdk version "17.0.20.1" 2026-08-18 LTS` | Dòng đầu của `java -version` mà preflight dùng; tách số major (Mục 4.3) làm phương án dự phòng khi chọn JDK |
| `testing_framework` | string | `junit4`, `junit5`, `testng` | Gợi ý; khối 1 tự dò lại |
| `revision_verification_status` | string | `CONTENT_MATCHED`, `UPSTREAM_PINNED` | Mức chắc chắn của commit (giải thích dưới bảng); báo cáo tách hai nhóm theo trường này |
| `revision_provenance` | string | `git_history`, `dataset_record` | Commit được lấy từ đâu (giải thích dưới bảng) |

Số liệu pilot hiện tại: Classes2Test 50 mẫu đều `git_history` + `CONTENT_MATCHED`; GitBug-Java 9 mẫu đều `dataset_record` + `UPSTREAM_PINNED`. 14/50 mẫu Classes2Test và 9/9 mẫu GitBug-Java có `module_path = "."`. `testing_framework`: 42 junit4, 6 junit5, 2 testng (Classes2Test); 8 junit5, 1 junit4 (GitBug-Java).

**Hai trường về commit:**

| Trường | Giá trị | Ý nghĩa |
| --- | --- | --- |
| `revision_provenance` (commit lấy từ đâu) | `dataset_record` | Dataset gốc ghi sẵn commit (GitBug-Java: commit đã fix của bug) |
| | `git_history` | Dataset gốc không có commit; preflight tìm trong lịch sử git của repository (Classes2Test) |
| `revision_verification_status` (chắc chắn tới mức nào) | `UPSTREAM_PINNED` | Đúng commit dataset ghi |
| | `CONTENT_MATCHED` | Commit có thân focal method và test case khớp với bản ghi của dataset (sau khi bỏ chú thích, chuẩn hóa khoảng trắng). Tái lập được nhưng **không bảo đảm** là đúng commit tác giả dataset đã dùng; file build và dependencies có thể khác |

`CLAUDE.md` yêu cầu báo cáo kết quả Classes2Test (content-matched) và GitBug-Java (upstream-pinned) **tách riêng**. Khối 1 chép cả hai trường vào M; khối 4 tách bảng theo `revision_verification_status` (và theo dataset).

**`class_fqn` (FQN) là gì.** FQN = *fully qualified name*, tên đầy đủ của class gồm package: class `OrderService` trong package `com.acme.shop` có FQN `com.acme.shop.OrderService`. Framework dùng FQN để chạy đúng một class test, để đo coverage và mutation **chỉ cho CUT và class lồng của nó** (mẫu chính xác `com.acme.shop.OrderService` và `com.acme.shop.OrderService$*`, spec 04 Mục 4), và để chạy EvoSuite (`-class com.acme.shop.OrderService`). Tên ngắn không đủ vì hai package có thể có hai class cùng tên.

Thiếu `task_id`, `repo_url`, `checkout_sha`, `class_path` hoặc `class_fqn` → `B1_MANIFEST_INVALID`, task bị `EXCLUDED`.

### 1.2. Cấu hình

Từ `machine.local.json`: `jdk_homes`, `maven_path`, `gradle_path`, `git_path`, `work_dir`. Từ experiment: `verification.baseline_runs` (mặc định 2), `verification.step_timeout_seconds`, `verification.infra_attempts` (3), `screening.require_paired_test_pass` (Mục 6.3).

## 2. Output

### 2.1. `block1/metadata.json`: Repository Metadata (M)

Mọi đường dẫn trong M **tính từ gốc repository**, dùng `/`. Ví dụ dưới dùng đường dẫn thật của mẫu pilot `118477931`; phiên bản, dependency và mức Java khai báo là **giá trị minh họa**.

```json
{
  "task_id": "118477931_429246acc7725217",
  "dataset": "classes2test",
  "revision": { "repo_url": "https://github.com/…", "commit": "…", "provenance": "git_history", "verification_status": "CONTENT_MATCHED" },
  "build_tool": { "kind": "maven", "version": "3.9.6", "wrapper": false, "build_root": "3rdParty/libsodium-jni-2.0.1", "build_mode": "standalone", "build_file": "3rdParty/libsodium-jni-2.0.1/pom.xml", "reactor_module": null },
  "java": { "declared": "1.7", "declared_source": "maven.compiler.source", "major": 7, "jdk_major": 8, "jdk_choice_reason": "declared" },
  "dependencies": {
    "direct": [ { "group": "com.github.jnr", "artifact": "jnr-ffi", "version": "2.0.9", "scope": "compile" } ],
    "test": [ { "group": "junit", "artifact": "junit", "version": "4.12", "scope": "test" } ],
    "resolved_count": 23
  },
  "test_framework": { "name": "junit4", "version": "4.12", "mocking": [], "assertion_libs": [], "evidence": "dependency junit:junit (test)" },
  "project_structure": {
    "multi_module": false,
    "modules": [ "." ],
    "target_module": ".",
    "module_root": "3rdParty/libsodium-jni-2.0.1",
    "packages": [ "org.libsodium.jni", "org.libsodium.jni.crypto" ]
  },
  "paths": {
    "source_root": "3rdParty/libsodium-jni-2.0.1/src/main/java",
    "test_root": "3rdParty/libsodium-jni-2.0.1/src/test/java",
    "cut_path": "3rdParty/libsodium-jni-2.0.1/src/main/java/org/libsodium/jni/crypto/Util.java",
    "generated_test_path": "3rdParty/libsodium-jni-2.0.1/src/test/java/org/libsodium/jni/crypto/Util_ArrowTest.java",
    "generated_test_fqn": "org.libsodium.jni.crypto.Util_ArrowTest"
  },
  "cut": { "fqn": "org.libsodium.jni.crypto.Util", "simple_name": "Util", "package": "org.libsodium.jni.crypto", "source_sha256": "…" }
}
```

M chứa đúng **6 thông tin** của Đóng góp 1: `build_tool`, `java`, `dependencies`, `test_framework`, `project_structure`, `paths` (source path và test path). `build_root`, `module_root` nằm trong 6 nhóm đó vì chúng là một phần của cấu trúc dự án. M **không** chứa test người viết hay phong cách test sẵn có (đã bỏ Existing Test Style).

### 2.2. `block1/baseline.json`

```json
{
  "runs": 2,
  "commands": { "compile": ["mvn", "-f", "pom.xml", "…", "-DskipTests", "test-compile"], "module_test": ["mvn", "-f", "pom.xml", "…", "test"] },
  "working_dir": "3rdParty/libsodium-jni-2.0.1",
  "passing_in_all_runs": [ "org.libsodium.jni.crypto.UtilTest#toBin", "…" ],
  "flaky": [],
  "failing_in_all_runs": [],
  "paired_test": { "class": "org.libsodium.jni.crypto.UtilTest", "tests_run": [ 6, 6 ], "status": "PASS" },
  "durations_seconds": { "compile": 12.4, "module_test": [ 30.1, 28.7 ] }
}
```

Khối 3 coi một test sẵn có là **regression** khi nó nằm trong `passing_in_all_runs` nhưng fail hoặc biến mất khi chạy cùng test sinh ra. Test `flaky` và `failing_in_all_runs` không dùng để kết luận regression.

### 2.3. `block1/workspace.json`

```json
{
  "mirror": "repo/mirrors/github.com/…/….git",
  "base_checkout": "repo/ws/<run>/<task>/base",
  "compiled_snapshot": "repo/ws/<run>/<task>/snapshot",
  "jdk_major": 8, "jdk_home": "…",
  "build_root": "3rdParty/libsodium-jni-2.0.1",
  "build_mode": "standalone",
  "build_root_source": "manifest:test_compile",
  "maven_module_selector": null,
  "common_flags": [ "-B", "-ntp", "…" ]
}
```

- `base_checkout`: mã ở đúng commit, **không bao giờ build hay chạy test trong đây**.
- `compiled_snapshot`: bản sao của `base_checkout` **sau khi biên dịch xong** (Mục 6.1), **chưa chạy test nào**. Mọi lần chạy test (baseline, mỗi lần xác minh của khối 3, mỗi lần đo của khối 4) đều làm trên **một bản sao mới** của snapshot này (spec 03 Mục 3), nên không lần chạy nào thấy file mà lần chạy trước tạo ra hay xóa đi.

### 2.4. Kết quả khi task không dùng được

`block1/outcome.json`: `{ "outcome": "EXCLUDED" | "INFRA", "code": "B1_…", "message": "…", "log": "logs/…", "needs_rerun": false }`. Task `EXCLUDED` không đi tiếp vào khối 2–4 và được liệt kê trong báo cáo với lý do.

## 3. Flow 1.1: Input Repository (tải đúng commit)

1. **Mirror:** `work_dir/mirrors/<host>/<owner>/<repo>.git`. Chưa có thì `git init --bare` rồi `git remote add origin <repo_url>`.
2. **Tải commit:** `git fetch --depth 1 origin <checkout_sha>`. Lỗi mạng: tối đa 3 lần thử (chờ 10 s, 30 s); vẫn lỗi → `INFRA`, `B1_CLONE_FAILED`, `needs_rerun`. Server từ chối fetch theo SHA hoặc không còn commit → thử `git fetch --unshallow`; vẫn không có → `EXCLUDED`, `B1_COMMIT_MISSING`.
3. **Checkout gốc:** `git worktree add --detach work_dir/ws/<run_id>/<task_id>/base <checkout_sha>`; kiểm tra `git rev-parse HEAD` = `checkout_sha`.
4. **Kiểm tra file CUT:** `class_path` phải tồn tại; không có → `EXCLUDED`, `B1_CUT_MISSING`. Ghi `source_sha256` của CUT.
5. **Đường dẫn:** `work_dir` không có dấu cách hay ký tự không phải ASCII; vi phạm thì `doctor` báo lỗi và `run` dừng trước khi chạy. Framework dùng **đường dẫn thật** của `work_dir` (đã giải symlink, ví dụ `/var` → `/private/var` trên macOS) vì Gradle in đường dẫn đã giải symlink; nếu không, không khớp được thư mục project Gradle với workspace.
6. **Mirror có sẵn theo cách đặt tên cũ** (`work_dir/mirrors/<owner>__<repo>.git`, của pilot_tool) được dùng làm nguồn tải cục bộ trước khi tải qua mạng; không có commit thì tải từ `origin` như bước 2.

## 4. Flow 1.2: Repository Analysis

### 4.1. Build root, bộ chọn module, module root

Hai bài học từ dữ liệu:
- Build root **không nhất thiết** là gốc repository (pilot `118477931`: mọi lệnh preflight chạy trong `3rdParty/libsodium-jni-2.0.1`; gốc repository không có `pom.xml`).
- Lệnh **reactor từ gốc** (`-pl <module> -am`) **có thể thất bại** trong khi lệnh **chạy riêng trong thư mục module** lại thành công: preflight khi đó chạy thêm bước `*_fallback`. Tập đánh giá Classes2Test có 2 CUT như vậy: `103728471_1928343239506019` (`integrating/spring`) và `9580168_f12b2e65e420fb2d` (`qulice-maven-plugin/src/it/multi-module/mod-alpha`, một project mẫu trong thư mục test tích hợp, không thuộc reactor gốc). Nếu chỉ lấy lần thử đầu, framework sẽ lặp lại đúng lệnh đã thất bại.

**Quy tắc: dùng đúng cách build đã biên dịch test thành công ở preflight.**

1. **Từ manifest** (cách chính). Trong `build_attempts`, chọn lần thử **biên dịch test thành công** (`exit_code = 0`) theo thứ tự ưu tiên `test_compile` → `test_compile_fallback` → `probe_compile` → `probe_compile_fallback`. Từ lần thử đó lấy:
   - `build_root` = `working_directory` của lần thử, tính từ `working_directory` của bản ghi (gốc repository), ví dụ `.`, `3rdParty/libsodium-jni-2.0.1`, `integrating/spring`;
   - **bộ chọn module**: lệnh có `-pl <X>` thì dùng `-pl <X> -am` (`build_mode = reactor`); lệnh không có `-pl` thì không dùng (`build_mode = standalone`). Gradle: lấy đường dẫn project từ task trong lệnh (`:a:b:compileTestJava` → `:a:b`), không có thì project gốc; sau khi đọc danh sách project (init script), nếu project chứa CUT (project con có thư mục là tiền tố dài nhất của source root) khác project đó thì dùng project chứa CUT, vì task không ghi project (`testClasses`) chạy cho **mọi** project và task của project cha cũng biên dịch project con (bổ sung 15/10/2026: 5 CUT của tập đánh giá bị loại nhầm `B1_MODULE_MISMATCH`);
   - `build_root_source = "manifest:<stage>"`.
2. **Không có lần thử thành công trong manifest** (manifest không có `build_attempts`): tự dò như preflight, dừng ở cách đầu tiên biên dịch test được:
   1. *Reactor*: đi lên từ module root (bước 3) tới gốc repository, lấy thư mục **cao nhất** liên tiếp có file build nhận thư mục con trên đường đi là module (Maven `<modules>`; Gradle `settings.gradle(.kts)` `include`); chạy `-pl <đường dẫn module> -am -DskipTests test-compile` ở đó.
   2. *Standalone*: chạy `-DskipTests test-compile` ngay trong module root.
   3. Cả hai thất bại với mọi JDK (Mục 4.3) → `EXCLUDED`, `B1_BASELINE_BUILD_FAILED`. Không tìm được thư mục nào có `pom.xml`, `build.gradle(.kts)` hay `settings.gradle(.kts)` → `EXCLUDED`, `B1_BUILD_ROOT_NOT_FOUND`.
   Ghi `build_root_source = "detected:reactor"` hoặc `"detected:standalone"`.
3. **Module root** = thư mục chứa `src/main/java` (hoặc thư mục source khai báo trong file build) của `class_path`. Kiểm tra: `build_mode = standalone` thì module root phải bằng build root; `reactor` thì module root phải bằng build root + đường dẫn trong `-pl`. Không khớp → `EXCLUDED`, `B1_MODULE_MISMATCH` (ghi cả hai đường dẫn để kiểm tra tay).
4. Mọi lệnh Maven/Gradle về sau (baseline, khối 3, khối 4) chạy **từ build root với đúng bộ chọn module** này. Ghi `build_root`, `build_mode`, `maven_module_selector` vào M và `workspace.json`.

| Mẫu | Lần thử thành công trong manifest | build root | Bộ chọn | Chế độ |
| --- | --- | --- | --- | --- |
| `118477931_…` (pilot) | `test_compile` trong `3rdParty/libsodium-jni-2.0.1`, lệnh không có `-pl` | `3rdParty/libsodium-jni-2.0.1` | – | standalone |
| `103728471_…` (tập đánh giá) | `test_compile` ở `.` với `-pl integrating/spring -am` **thất bại**; `test_compile_fallback` trong `integrating/spring` thành công | `integrating/spring` | – | standalone |
| Ví dụ `shop` (minh họa) | `test_compile` ở `.` với `-pl shop-core -am` thành công | `.` | `-pl shop-core -am` | reactor |

### 4.2. Build tool

1. File build ở build root: `pom.xml` → Maven; `build.gradle(.kts)` hoặc `settings.gradle(.kts)` → Gradle. Có cả hai thì theo `build_tool` của manifest.
2. Chương trình build: **theo đúng lệnh đã biên dịch thành công ở preflight** (Mục 4.1): lệnh dùng `mvnw`/`gradlew` thì dùng wrapper ở build root (hoặc gốc repository nếu build root không có), lệnh dùng `mvn`/`gradle` thì dùng `maven_path`/`gradle_path` của máy. Manifest không có lệnh thành công (tự dò) thì dùng wrapper nếu có. Wrapper được đặt quyền chạy trên macOS/Linux và lưu **tương đối với gốc repository** để chạy được trên mọi bản sao (snapshot, bản sao cho từng lần chạy test). Lý do theo lệnh preflight thay vì luôn ưu tiên wrapper: lặp lại đúng cách build đã thành công (wrapper cũ có thể tải bản Maven/Gradle không chạy được với JDK hiện tại).
3. Phiên bản: `mvn -v` / `gradle -v` với JDK đã chọn.

### 4.3. Java version và chọn JDK

1. **Mức Java khai báo**, lấy giá trị đầu tiên có:
   - Maven: effective POM của module (`mvn <cờ chung> [-pl m -am] org.apache.maven.plugins:maven-help-plugin:3.4.1:effective-pom -Doutput=<file ngoài repository>`; Maven cũ hơn 3.6.3 dùng `3.2.0`), chạy **một lần gọi riêng trước khi biên dịch**, bằng JDK của manifest, vì cần mức Java để chọn JDK. Với `-am` file có nhiều `<project>`; project của module đích là project có `target` hoặc `src/main/java` nằm ngay trong module root. Thứ tự lấy: `maven.compiler.release` → `maven.compiler.source` → `release`/`source` của `maven-compiler-plugin` → thuộc tính `java.version`; giá trị chưa nội suy (`${…}`) bị bỏ qua.
   - Gradle: init script của framework (task `arrowInfo`, chạy trước khi biên dịch) in `java.toolchain.languageVersion`, nếu không có thì `sourceCompatibility` của project module, cùng thư mục project, source/test dirs và dependency khai báo.
   - Lệnh effective POM hoặc `arrowInfo` lỗi: không loại task, ghi `missing_reasons.java` và chọn JDK theo manifest.
   - Quy về số major: `1.7` → 7, `1.8` → 8, `11` → 11.
2. **Major từ manifest** (`java_version`): lấy chuỗi trong ngoặc kép sau `version` của dòng `java -version`; nếu bắt đầu bằng `1.` thì major là số thứ hai (`"1.8.0_504"` → 8), nếu không thì là số đầu (`"17.0.20.1"` → 17, `"11.0.32.1"` → 11, `"21"` → 21). Không tách được → `null`.
3. **Chọn JDK** trong các JDK có trên máy (8, 11, 17, 21): JDK nhỏ nhất có major ≥ mức khai báo (mức khai báo < 8 thì dùng 8). Không đọc được mức khai báo → JDK của manifest (`jdk_choice_reason = "manifest"`).
4. Biên dịch baseline (Mục 6.1) thất bại vì lỗi biên dịch → thử JDK của manifest, rồi lần lượt các JDK còn lại; JDK nào biên dịch được thì dùng (`jdk_choice_reason = "fallback:<các JDK đã thử>"`). Không JDK nào được → `EXCLUDED`, `B1_BASELINE_BUILD_FAILED`.
5. **JVM chạy test không khởi động được** với JDK đã chọn (chốt 14/10/2026): lần chạy baseline đầu tiên không có test nào trong báo cáo **và** log có dấu hiệu JVM không khởi động (`stack size specified is too small`, `Could not create the Java Virtual Machine`, `Error occurred during initialization of VM`, `Unrecognized VM option`, `The forked VM terminated without properly saying goodbye`) → biên dịch lại bằng JDK kế tiếp theo thứ tự ở bước 4 (bỏ JDK vừa lỗi) và chạy baseline lại; `jdk_choice_reason = "fallback:<các JDK đã thử>"`. Không JDK nào khác biên dịch được thì giữ JDK ban đầu. Ví dụ: jsoup (GitBug-Java pilot) đặt `argLine -Xss256k`; JDK 8 bản Zulu trên Mac ARM đòi stack ít nhất 640k nên không chạy được test (`NO_TEST_RUN`), sau quy tắc này dùng JDK 17 và đạt tiêu chí B, giống kết quả trên máy Windows. Mục đích: kết quả khối 1 không phụ thuộc kiến trúc máy.

### 4.4. Dependencies

1. **Trực tiếp:** Maven: `dependencies` trong effective POM của module; Gradle: init script in dependency khai báo của `implementation`, `api`, `compileOnly`, `testImplementation`, `testRuntimeOnly`.
2. **Đã phân giải và classpath:** Maven phải chạy **trong cùng một lần gọi Maven** với `test-compile`, vì `dependency:list`/`build-classpath` cần phân giải artifact của module anh em, và Maven chỉ phân giải chúng từ reactor khi chúng được build **trong cùng lần gọi** (gọi `dependency:list` riêng, dù có `-pl … -am`, sẽ báo thiếu artifact; đã kiểm chứng bằng fixture hai module, tool cũ cũng ghép như vậy):
   `mvn -f pom.xml [-pl <m> -am] <cờ chung> -DskipTests test-compile <dependency-plugin>:list -DincludeScope=test -DoutputAbsoluteArtifactFilename=true -DoutputFile=arrow-deps.txt -DappendOutput=false <dependency-plugin>:build-classpath -Dmdep.includeScope=test -Dmdep.outputFile=arrow-cp.txt`
   - `<dependency-plugin>` ghim phiên bản (`org.apache.maven.plugins:maven-dependency-plugin:3.8.1`; Maven cũ hơn 3.6.3 dùng `3.1.2`).
   - Đường dẫn file đầu ra là tương đối nên mỗi module trong reactor ghi vào thư mục của nó; framework đọc file của **module đích**.
   - Gradle: init script của framework in `testRuntimeClasspath` của project đích (Gradle tự build project phụ thuộc).
   Ghi `resolved_count`, danh sách dependency vào `block1/dependencies_resolved.txt` và **classpath test** vào `block1/test_classpath.txt`: jar ngoài ghi đường dẫn tuyệt đối (trong `~/.m2`, `~/.gradle`), thư mục class của module anh em ghi **tương đối với gốc workspace** (để dùng được trên mọi bản sao). Khối 3 dùng danh sách này khi trích API class thư viện; khối 4 dùng classpath này cho PIT và EvoSuite.
   - **Quy tắc chung cho mọi khối:** bước Maven nào cần phân giải artifact của module anh em (liệt kê dependency, lấy classpath) đều chạy trong **cùng một lần gọi Maven** với `test-compile`.
3. Chia `direct` (scope compile/runtime/provided) và `test` (scope test).
4. Lỗi tải dependency sau 3 lần thử → `INFRA`, `B1_DEPENDENCY_RESOLUTION`, `needs_rerun`.

### 4.5. Test framework

| Dấu hiệu trong dependency scope test | `name` |
| --- | --- |
| `org.junit.jupiter:*`, không có `junit-vintage-engine` | `junit5` |
| `org.junit.jupiter:*` và `junit-vintage-engine` | `junit5` (`vintage: true`: test JUnit 4 vẫn chạy được) |
| `junit:junit` | `junit4` |
| `org.testng:testng` | `testng` |

Có cả TestNG và JUnit: chọn theo framework của **test người viết** (`test_class_path`: import `org.testng` hay `org.junit`). Không có dấu hiệu nào → `testing_framework` của manifest (`evidence = "manifest"`). Ghi thêm thư viện mock (`mockito-*`, `easymock`, `jmockit`, `powermock-*`) và assertion (`assertj-core`, `hamcrest*`, `truth`).

Ảnh hưởng của `testng` tới các khối sau: khối 2 đưa "TestNG" vào khối M; khối 3 có khung DISCOVERY cho TestNG (spec 03 Mục 6.5) và đọc báo cáo Surefire/Gradle như JUnit; khối 4 thêm `pitest-testng-plugin` cho PIT (spec 04 Mục 4.3); EvoSuite chỉ sinh JUnit 4 nên bộ EvoSuite thường không biên dịch được trong module chỉ có TestNG (ghi lý do, spec 04 Mục 3).

### 4.6. Project structure

1. Danh sách module trong build root: Maven `<modules>` (đệ quy); Gradle `settings.gradle(.kts)` `include`.
2. `target_module` = `module_path`; `module_root` theo Mục 4.1.
3. `packages`: các package có file `.java` trong `source_root`, tối đa 30, ưu tiên package của CUT và các package cha/con.

### 4.7. Source / test paths

1. `source_root` = `class_path` bỏ phần `<package path>/<File>.java` (ví dụ `3rdParty/libsodium-jni-2.0.1/src/main/java`).
2. `test_root`: Maven `testSourceDirectory` trong effective POM, mặc định `<module_root>/src/test/java`; Gradle `sourceSets.test.java.srcDirs` qua init script, mặc định `<module_root>/src/test/java`.
3. `generated_test_path` = `<test_root>/<package path>/<CUT>_ArrowTest.java`. Tên kết thúc bằng `Test` nên khớp mẫu mặc định của Surefire và Gradle. File phải chưa tồn tại; trùng (rất hiếm) thì dùng `_ArrowTest2`.
4. Test path dùng để (a) đưa vào prompt cho LLM khai báo đúng package và tên class, (b) khối 3 ghi file test vào đúng chỗ. Không dùng để đọc test cũ gửi cho LLM.

## 5. Flow 1.3: Configuration Output

1. Dựng M theo Mục 2.1; đủ 6 nhóm thông tin (nhóm nào không có thì `null` kèm `missing_reason`).
2. Ghi `block1/metadata.json` và khối M dạng chữ để đưa vào prompt (`block1/metadata_prompt.txt`, định dạng ở spec 02 Mục 2.2), sinh bằng code từ M.
3. **Chưa ghi ledger** ở đây: khối 1 chỉ coi là xong sau Mục 6.4.

## 6. Flow 1.4: Baseline

**Cờ build dùng chung** (giống nhau cho mọi khối, mọi hệ thống, ghi vào `run.lock.json`):
- Maven: `-f pom.xml` (chạy từ build root), `-B -ntp -Dstyle.color=never`, tắt checkstyle, spotless, formatter, impsort, license, rat, jacoco (của dự án), pmd, cpd, spotbugs, enforcer, javadoc, gpg. Trên Windows thêm `-Dsurefire.useManifestOnlyJar=false`.
- Gradle: `--no-daemon --console=plain -Dorg.gradle.configuration-cache=false -Dorg.gradle.unsafe.configuration-cache=false`, loại các task `checkstyle*`, `spotless*` nếu có. Configuration cache bị tắt vì task trong init script của framework tham chiếu `Project` nên không lưu vào cache được (bổ sung 15/10/2026: dự án GitBug-Java bật configuration cache trong `gradle.properties` bị loại nhầm); tắt cache không đổi cách biên dịch hay chạy test.
- JVM: `JAVA_TOOL_OPTIONS = -Dfile.encoding=UTF-8 -Duser.language=en -Duser.country=US -Duser.timezone=UTC`.

**Lệnh Maven luôn chạy từ build root, kèm bộ chọn module của Mục 4.1** (`-pl <module> -am` ở chế độ reactor, không có ở chế độ standalone) cho **mọi** bước: biên dịch, chạy riêng test, chạy test của module (baseline và khối 3), đo (khối 4). Nhờ `-am`, các module anh em mà module đích phụ thuộc luôn được build trong cùng reactor, không cần `install` vào `~/.m2`. Hệ quả: lần "chạy test của module" cũng chạy test của các module anh em đó; baseline và khối 3 dùng cùng lệnh nên danh sách so sánh vẫn nhất quán (test sinh ra nằm trong module đích nên không ảnh hưởng tới test của module anh em, chỉ tốn thêm thời gian). Gradle: `:<project>:test` tự build project phụ thuộc.

### 6.1. Biên dịch và snapshot

1. Sao chép `base_checkout` sang thư mục build tạm; biên dịch **và** lấy dependency, classpath trong **một lần gọi** (lệnh ở Mục 4.4 bước 2: `-DskipTests test-compile …:list …:build-classpath`, với bộ chọn module); Gradle `:<project>:testClasses` rồi init script in classpath. Thất bại vì lỗi biên dịch → thử JDK khác (Mục 4.3 bước 4).
2. Biên dịch xong → đổi thư mục này thành `compiled_snapshot` (chưa chạy test nào trong đó).

### 6.2. Chạy test sẵn có ×2

1. Mỗi lần chạy: tạo **bản sao mới** của `compiled_snapshot`, chạy Maven `-DfailIfNoTests=false -Dsurefire.failIfNoSpecifiedTests=false -Dmaven.test.failure.ignore=true test` (với bộ chọn module); Gradle `:<project>:test --continue` (init script đặt `ignoreFailures = true` cho task test). Đọc báo cáo XML (`target/surefire-reports`, `build/test-results/**`) của mọi module trong reactor. Xóa bản sao sau khi đọc.
   - **Tên class của một test case:** Surefire 3.x với JUnit 5 có thể ghi thuộc tính `classname` của `<testcase>` là tên method hoặc display name (`testInsert()`; gặp ở mẫu pilot `9577793_…`, `93861612_…`). Framework chỉ dùng `classname` khi nó là tên class Java hợp lệ (và có package nếu tên `<testsuite>` có package), ngược lại dùng tên `<testsuite>` (luôn là FQN của class test). Class lồng (`Outer$Inner`) thuộc về class ngoài khi tính tiêu chí B.
   - **Jar hỏng trong cache của máy:** javac báo `error reading <jar>; zip END header not found` (hoặc `error in opening zip file`) với jar trong `~/.m2/repository` hay `~/.gradle/caches` mà không còn là file zip (thường là trang HTML do kho Maven bên thứ ba trả về thay cho file) → xóa đúng các jar đó và coi là lỗi hạ tầng để thử lại (công cụ build tải lại). Áp dụng cho mọi lệnh build của khối 1, 3, 4 (bổ sung 15/10/2026: 75 jar như vậy trên máy Dung-mac làm 5 CUT của tập đánh giá ra `B1_BASELINE_BUILD_FAILED`).
- **File báo cáo được đọc:** chỉ `TEST-*.xml` nằm ngay trong `surefire-reports/` (Maven) hoặc `test-results/<task>/` (Gradle). TestNG ghi thêm bản trùng vào `surefire-reports/junitreports/`; đọc cả hai thì mỗi test bị đếm hai lần (mẫu pilot `618492_…`, `43258821_…`). `tests_run` của tiêu chí B đếm số test **khác nhau** (`Class#method`) mỗi lần chạy.
2. Chạy `baseline_runs` lần (mặc định 2). Pass ở mọi lần → `passing_in_all_runs`; pass ở một số lần → `flaky`; fail ở mọi lần → `failing_in_all_runs`.
3. Module không có test sẵn có nào → danh sách rỗng (bước module regression của khối 3 khi đó chỉ kiểm tra build và test sinh ra).
4. Timeout hoặc lỗi hạ tầng → tối đa 3 lần thử; vẫn lỗi → `INFRA`, `B1_BASELINE_TIMEOUT` / `B1_DEPENDENCY_RESOLUTION`, `needs_rerun`; task vào danh sách "Cần chạy lại riêng" kèm lệnh `arrow run --run-id <run> --tasks <task_id> --rerun-infra`.

### 6.3. Tiêu chí B (sàng lọc mẫu)

Theo `evaluation_manifest/pilot/README.md` (pilot v2, 13/10/2026): CUT đạt khi (1) baseline READY và (2) test class người viết ghép với CUT (`test_class_path`) có **ít nhất một test chạy**, **không** test nào của class đó fail ở cả hai lần hoặc flaky. Test của class khác được phép fail sẵn (chúng đã bị loại khỏi so sánh regression). Tập đánh giá chính khi chọn lại cũng phải dùng đúng tiêu chí này để pilot đại diện cho tập đánh giá.

1. Từ hai lần chạy ở Mục 6.2, lấy các test thuộc class của `test_class_path`; ghi `paired_test.status`:

| `status` | Khi nào |
| --- | --- |
| `PASS` | ≥ 1 test chạy ở cả hai lần, mọi test pass ở cả hai lần |
| `NO_TEST_RUN` | Không test nào của class đó được chạy (bị `@Ignore`, sai mẫu tên, không có test) |
| `FAILING` | Có test fail ở cả hai lần |
| `FLAKY` | Có test lúc pass lúc fail |
| `NO_PAIRED_TEST` | Manifest không có `test_class_path` |

2. **Ai quyết định thay mẫu:** việc **thay** CUT không đạt bằng CUT khác làm **trước** framework, bằng script chọn mẫu (`preflight_tool/scripts/select_pilot_after_s0.py`), sinh manifest phiên bản mới (đúng quy định của `CLAUDE.md`: thay bằng class rút ngẫu nhiên từ pool theo cùng quy tắc, kèm phiên bản manifest mới). Framework chỉ **đo** tiêu chí B:
   - Lệnh `arrow screen --manifest <file> --run-id <id> [--dataset D] [--tasks a,b|FILE] [--workers N] [--rerun-infra] [--keep-workspace]` chạy **chỉ khối 1** cho mọi CUT của manifest (song song theo `workers.build`) và ghi `reports/screening.csv` (`task_id, outcome, code, paired_test_status, tests_run, jdk_major, message`; `tests_run` dạng `6;6`), làm đầu vào cho script chọn mẫu (`select_pilot_after_s0.py --screening-csv …`). Lệnh này chỉ **đo** tiêu chí B: CUT không đạt vẫn có `outcome = OK`, trạng thái nằm ở cột `paired_test_status`. Mỗi task ghi ledger khi xong, chạy lại cùng run id thì bỏ qua task đã xong (task INFRA chỉ chạy lại khi có `--rerun-infra`). Workspace (`base`, `snapshot`) bị xóa sau mỗi task trừ khi có `--keep-workspace`.
   - Khi chạy thực nghiệm với `screening.require_paired_test_pass = true` (mặc định cho pilot và tập đánh giá), CUT có `paired_test.status ≠ PASS` bị `EXCLUDED` với mã `B1_PAIRED_TEST_NOT_PASSING`. Manifest đã sàng lọc đúng thì trường hợp này không xảy ra; nếu xảy ra (ví dụ khác máy) thì báo cáo liệt kê để nhóm xử lý.

### 6.4. Kết thúc khối 1 và ghi ledger

Ledger là bằng chứng "job đã xong" để chạy tiếp bỏ qua job đó, nên **chỉ ghi khi mọi đầu ra đã lưu đủ**, theo đúng thứ tự:

1. `block1/metadata.json`, `metadata_prompt.txt`, `dependencies_resolved.txt`, `test_classpath.txt` (Mục 4–5);
2. `compiled_snapshot` hoàn chỉnh (Mục 6.1) và `block1/workspace.json`;
3. `block1/baseline.json` gồm cả `paired_test` (Mục 6.2–6.3);
4. `block1/outcome.json`;
5. **cuối cùng** dòng ledger `{ "block": 1, "task_id": …, "outcome": "OK" | "EXCLUDED" | "INFRA", "code": … }`.

Run dừng ở bất kỳ đâu trước bước 5 → khi chạy tiếp, task không có dòng ledger khối 1 nên **khối 1 chạy lại từ đầu**: xóa thư mục `block1/` và snapshot dở dang của task rồi làm lại (mirror đã tải được giữ, không tải lại). Không có trường hợp "có metadata nhưng thiếu baseline" mà vẫn được coi là xong.

## 7. Mã lỗi

| Mã | Kết quả | Khi nào |
| --- | --- | --- |
| `B1_MANIFEST_INVALID` | EXCLUDED | Thiếu trường bắt buộc |
| `B1_CLONE_FAILED` | INFRA, cần chạy lại | Lỗi mạng khi tải (3 lần thử) |
| `B1_COMMIT_MISSING` | EXCLUDED | Không lấy được commit |
| `B1_CUT_MISSING` | EXCLUDED | Không có file CUT ở commit |
| `B1_BUILD_ROOT_NOT_FOUND` | EXCLUDED | Không xác định được thư mục build |
| `B1_MODULE_MISMATCH` | EXCLUDED | `class_path` không nằm trong module nào xác định được |
| `B1_DEPENDENCY_RESOLUTION` | INFRA, cần chạy lại | Không tải được dependency (3 lần thử) |
| `B1_BASELINE_BUILD_FAILED` | EXCLUDED | Không JDK nào biên dịch được module |
| `B1_BASELINE_TIMEOUT` | INFRA, cần chạy lại | Build hoặc test sẵn có quá thời gian (3 lần thử) |
| `B1_PAIRED_TEST_NOT_PASSING` | EXCLUDED | Tiêu chí B không đạt khi `require_paired_test_pass = true` |
| `B1_WORKSPACE` | INFRA, cần chạy lại | Lỗi khi sao chép/xóa workspace hoặc không chạy được chương trình build (lỗi của máy, không phải của task) |

## 8. Interface C#

```csharp
public interface IRepositoryAnalyzer
{
    // Flow 1.1–1.4; không ném lỗi cho task hỏng, trả Outcome = EXCLUDED/INFRA.
    Task<RepositoryAnalysis> AnalyzeAsync(TaskRecord task, RunContext run, CancellationToken ct);
}

public sealed record RepositoryAnalysis(
    BlockOutcome Outcome,          // OK | EXCLUDED | INFRA, kèm mã, thông điệp, needs_rerun
    RepositoryMetadata? Metadata,  // M (Mục 2.1)
    Baseline? Baseline,            // Mục 2.2, gồm paired_test
    WorkspaceInfo? Workspace);     // Mục 2.3
```

## 9. Kiểm thử

- Unit: tách major từ `java_version` (`"1.8.0_504"` → 8, `"17.0.20.1" 2026-08-18 LTS` → 17, `"11.0.32.1"` → 11); chọn build root và bộ chọn từ **lần biên dịch test thành công** trong `build_attempts` (`118477931` → `3rdParty/libsodium-jni-2.0.1` standalone; `103728471` → `integrating/spring` standalone vì lệnh reactor thất bại); dò khi không có `build_attempts`; ledger khối 1 chỉ ghi sau baseline; module root và bộ chọn module; đọc Java version từ effective POM mẫu và output Gradle; chọn JDK; dò test framework (gồm TestNG, dự án có cả hai); `generated_test_path`; phân loại `paired_test.status`.
- Integration: 3 fixture (Maven JUnit 4, Maven đa module JUnit 5 có module phụ thuộc module anh em, Gradle JUnit 5), một fixture có build root nằm trong thư mục con, một fixture có module không thuộc reactor gốc (lệnh reactor thất bại, standalone thành công); kiểm tra `test-compile` + `dependency:list` + `build-classpath` trong một lần gọi trên fixture đa module: M đủ 6 thông tin, baseline đúng số test pass, tiêu chí B đúng.

## 10. Ví dụ

### 10.1. Ví dụ A: build root là thư mục con (mẫu pilot thật `118477931`)

Input (rút gọn từ manifest pilot; các trường như trong file thật):

```json
{
  "task_id": "118477931_429246acc7725217",
  "module_path": ".",
  "class_path": "3rdParty/libsodium-jni-2.0.1/src/main/java/org/libsodium/jni/crypto/Util.java",
  "test_class_path": "3rdParty/libsodium-jni-2.0.1/src/test/java/org/libsodium/jni/crypto/UtilTest.java",
  "working_directory": ".../workspaces/118477931_429246acc7725217",
  "build_attempts": [
    { "stage": "tool_version", "command": ["mvn", "--version"], "working_directory": ".../workspaces/118477931_429246acc7725217/3rdParty/libsodium-jni-2.0.1", "exit_code": 0 },
    { "stage": "test_compile", "command": ["mvn", "-q", "-DskipTests", "test-compile"], "working_directory": ".../workspaces/118477931_429246acc7725217/3rdParty/libsodium-jni-2.0.1", "exit_code": 0 }
  ],
  "build_tool": "maven",
  "java_version": "openjdk version \"1.8.0_504\"",
  "testing_framework": "junit4",
  "revision_provenance": "git_history",
  "revision_verification_status": "CONTENT_MATCHED"
}
```

| Bước | Framework làm | Kết quả |
| --- | --- | --- |
| 4.1 Build root | Lần `test_compile` thành công chạy trong `3rdParty/libsodium-jni-2.0.1`, lệnh không có `-pl` | build root `3rdParty/libsodium-jni-2.0.1`, chế độ standalone (gốc repository **không** có `pom.xml`; tìm từ gốc như bản spec đầu sẽ loại nhầm mẫu này) |
| 4.1 Module root | build root + `.` | `3rdParty/libsodium-jni-2.0.1`; `class_path` nằm trong `…/src/main/java` ✔ |
| 4.1 Bộ chọn module | Lệnh thành công không có `-pl` | Không dùng `-pl` |
| 4.3 Java | Manifest `"1.8.0_504"` → 8; effective POM khai báo (giả định) 1.7 → JDK nhỏ nhất ≥ 8 | `jdk_major = 8` |
| 6.1 Biên dịch | `mvn -f pom.xml -B … -DskipTests test-compile` trong `3rdParty/libsodium-jni-2.0.1` | OK → snapshot |
| 6.3 Tiêu chí B | `UtilTest`: các test chạy và pass ở cả 2 lần (mẫu này đã qua sàng lọc S0 của pilot v2) | `PASS` |

### 10.2. Ví dụ B: module con phụ thuộc module anh em (CUT minh họa, số liệu giả định)

Repository `https://github.com/acme/shop`: `pom.xml` gốc có `<modules>shop-api, shop-core</modules>`; `shop-core` phụ thuộc `shop-api`. Manifest: `module_path = "shop-core"`, `class_path = "shop-core/src/main/java/com/acme/shop/OrderService.java"`, `build_attempts[0].working_directory` = gốc repository.

| Bước | Framework làm | Kết quả |
| --- | --- | --- |
| 4.1 Build root | Lần `test_compile` thành công ở gốc repository với `-pl shop-core -am` | `.`, chế độ reactor |
| 4.1 Module root | `.` + `shop-core` | `shop-core` |
| 4.1 Bộ chọn module | Lấy từ lệnh thành công | `-pl shop-core -am` |
| 6.1 Biên dịch | `./mvnw -f pom.xml -B … -pl shop-core -am -DskipTests test-compile` | Build `shop-api` rồi `shop-core` trong cùng reactor ✔ |
| 6.2 Chạy test ×2 | `./mvnw -f pom.xml -B … -pl shop-core -am -DfailIfNoTests=false -Dmaven.test.failure.ignore=true test` | Chạy test của `shop-api` (8) và `shop-core` (42); lần 2 `ClockTest#now` fail → `flaky` |
| 6.3 Tiêu chí B | `OrderServiceTest`: 5 test pass cả 2 lần | `PASS` |

Nếu chạy test **không** có `-am` (như bản spec cũ ở khối 3), Maven sẽ tìm `shop-api` trong `~/.m2`, không thấy và báo lỗi thiếu artifact, dù bước biên dịch trước đó đã thành công.

Output `block1/metadata.json` của ví dụ B (rút gọn):

```json
{
  "task_id": "555001_ab12cd34ef56ab78", "dataset": "classes2test",
  "revision": { "repo_url": "https://github.com/acme/shop", "commit": "3f9a1c0e…", "provenance": "git_history", "verification_status": "CONTENT_MATCHED" },
  "build_tool": { "kind": "maven", "version": "3.9.6", "wrapper": true, "build_root": ".", "build_mode": "reactor", "build_file": "pom.xml", "reactor_module": "shop-core" },
  "java": { "declared": "1.8", "declared_source": "maven.compiler.source", "major": 8, "jdk_major": 8, "jdk_choice_reason": "declared" },
  "test_framework": { "name": "junit4", "version": "4.13.2", "mocking": [ "org.mockito:mockito-core:3.12.4" ], "assertion_libs": [], "evidence": "dependency junit:junit (test)" },
  "project_structure": { "multi_module": true, "modules": [ "shop-api", "shop-core" ], "target_module": "shop-core", "module_root": "shop-core", "packages": [ "com.acme.shop", "com.acme.shop.repo" ] },
  "paths": {
    "source_root": "shop-core/src/main/java", "test_root": "shop-core/src/test/java",
    "cut_path": "shop-core/src/main/java/com/acme/shop/OrderService.java",
    "generated_test_path": "shop-core/src/test/java/com/acme/shop/OrderService_ArrowTest.java",
    "generated_test_fqn": "com.acme.shop.OrderService_ArrowTest"
  }
}
```

### 10.3. Ví dụ C: lệnh reactor thất bại, build riêng module thành công (mẫu thật `103728471_1928343239506019`, tập đánh giá)

`build_attempts` trong manifest (rút gọn, đúng như file):

| stage | thư mục (tính từ gốc repository) | lệnh | exit |
| --- | --- | --- | ---: |
| `tool_version` | `.` | `mvn --version` | 0 |
| `main_compile` | `.` | `mvn -q -pl integrating/spring -am -DskipTests compile` | 1 |
| `main_compile_fallback` | `integrating/spring` | `mvn -q -DskipTests compile` | 0 |
| `test_compile` | `.` | `mvn -q -pl integrating/spring -am -DskipTests test-compile` | 1 |
| `test_compile_fallback` | `integrating/spring` | `mvn -q -DskipTests test-compile` | 0 |

Framework chọn lần biên dịch test thành công `test_compile_fallback` → build root `integrating/spring`, chế độ standalone, không `-pl`. Mọi lệnh sau chạy trong `integrating/spring`. Nếu lấy `build_attempts[0]` (thư mục `.`) và `-pl integrating/spring -am` như bản spec trước, khối 1 sẽ lặp lại đúng lệnh preflight đã thất bại và loại nhầm CUT này.

### 10.4. Ví dụ task bị loại

| Tình huống | Điều framework thấy | Output |
| --- | --- | --- |
| Repository đã bị xóa khỏi GitHub | `git fetch` trả "repository not found" | `EXCLUDED`, `B1_COMMIT_MISSING` |
| Mạng chập chờn | `git fetch` lỗi cả 3 lần thử | `INFRA`, `B1_CLONE_FAILED`, vào danh sách cần chạy lại |
| Module không biên dịch được với JDK 8, 11, 17, 21 | Mọi lần `test-compile` mã thoát 1 | `EXCLUDED`, `B1_BASELINE_BUILD_FAILED` |
| Test người viết bị `@Ignore` hết | `paired_test.status = NO_TEST_RUN` | `EXCLUDED`, `B1_PAIRED_TEST_NOT_PASSING` (khi bật `require_paired_test_pass`) |
