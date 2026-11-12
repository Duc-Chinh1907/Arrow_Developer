# P11. Kiểm thử, fixture, CI và đóng gói

**Mục đích:** quy định cách kiểm thử hệ thống (các mức test, fixture, test giả lập LLM), quy trình CI, cách đóng gói và phát hành, và bộ repo thật dùng để kiểm chứng trước khi phát hành.

**Nguồn gốc:**
- các mức test theo R00 §8;
- fixture và test của `ARROW_Framework` được mang sang làm **bộ đối chiếu**: code viết mới phải cho kết quả giống code cũ trên cùng đầu vào (Mục 4).

## 1. Các mức test

| Mức | Project | Chạy ở đâu | Cần gì | Thời gian mục tiêu |
|---|---|---|---|---|
| Unit | `Arrow.Business.Tests`, `Arrow.DataAccess.Tests`, `Arrow.Cli.Tests` | Mọi PR, Windows và Ubuntu | Chỉ .NET | Dưới 2 phút |
| Integration | `Arrow.IntegrationTests` (`[Trait("Category","Integration")]`) | Mọi PR, Windows và Ubuntu | JDK 8/11/17/21, Maven, Gradle, java-helper | Dưới 25 phút |
| E2E | `Arrow.IntegrationTests` (`[Trait("Category","E2E")]`) | Mọi PR (Ubuntu), mỗi đêm (cả hai) | Như trên, kèm file thực thi `arrow` | Dưới 15 phút |
| java-helper | `tools/java-helper` (JUnit 5, `mvn test`) | Mọi PR | JDK 17, Maven | Dưới 2 phút |
| Smoke với model thật | `scripts/smoke-real.ps1` / `.sh` | Chạy tay; trước mỗi bản phát hành | Khoá trong `ARROW_SECRET_<REF>` | Khoảng 10 phút |
| Kiểm chứng trên repo thật | `scripts/validate.ps1` / `.sh` (Mục 6) | Chạy tay ở GĐ7 và trước mỗi bản phát hành lớn | Model thật, mạng | Vài giờ |

**Quy tắc chung:**
- Không test tự động nào gọi model thật hay cần khoá API. Chúng dùng `FakeLlmClient` (Mục 3).
- Test không phụ thuộc thứ tự và chạy song song được. Mỗi test dùng `ARROW_HOME` riêng trong thư mục tạm, xoá khi xong.
- Test nào cần công cụ thiếu (ví dụ không có JDK 8) thì **bỏ qua kèm lý do** (`Skip`), không fail. CI luôn cài đủ để không có test bị bỏ qua.

## 2. Fixture

Nằm ở `tests/fixtures/`. Mỗi fixture có `README.md` ghi:
- mục đích;
- CUT gợi ý;
- baseline mong đợi (số test pass, fail, flaky);
- đường dẫn `generated_test_path` mong đợi;
- khoảng giá trị chỉ số khối 4 với bộ test mẫu.

| Fixture | Nguồn | Kiểm tra gì |
|---|---|---|
| `maven-verify-junit4` | `ARROW_Framework` | Xác minh 5 bước; CUT `PriceCalculator`; `TotalsTest` phụ thuộc biến static `Invoice.defaultRate` để tạo `REGRESSION` |
| `maven-subdir-junit4` | `ARROW_Framework` | Gốc repo không có file build, build root là `lib/`; có `BrokenTest` fail ở mọi lần chạy (`failing_in_all_runs`) |
| `maven-multi-junit5` | `ARROW_Framework` | Reactor hai module (`shop-core` phụ thuộc `shop-api`), `-pl … -am`; project `samples/standalone` không thuộc reactor (reactor lỗi, standalone thành công) |
| `maven-small-stack` | `ARROW_Framework` | Surefire `-Xss256k`: đổi JDK khi JVM chạy test không khởi động được |
| `gradle-junit5` | `ARROW_Framework` | Gradle không có wrapper, project `:core`, Java 17 |
| `maven-no-tests` | Mới | Module không có test nào: baseline rỗng vẫn `OK` |
| `maven-testng` | Mới | TestNG: khung DISCOVERY cho TestNG, plugin PIT cho TestNG |
| `maven-wrapper-junit5` | Mới | Có `mvnw`: ưu tiên wrapper (P01) |
| `maven-existing-arrowtest` | Mới | Đã có `<CUT>_ArrowTest.java`, nên phải sinh `_Arrow2Test` (P01 §4.7, P06) |

**Quy tắc:**
- Fixture nhỏ, chỉ dùng dependency phổ biến (JUnit, Mockito, AssertJ) để CI tải nhanh. CI cache `~/.m2` và `~/.gradle`.
- Test **không bao giờ** chạy trực tiếp trên `tests/fixtures/`. Trước mỗi test, fixture được chép vào thư mục tạm, rồi so hash của bản gốc để chắc không có gì bị sửa.
- Mỗi fixture có một **bộ test mẫu VALID** viết tay, đặt trong `tests/fixtures/<f>/.arrow-sample/`, để test khối 4 và P06 không cần LLM.

## 3. Giả lập LLM

### 3.1 `FakeLlmClient`

`FakeLlmClient` (viết mới, tham khảo bản của `ARROW_Framework`) trả lời theo **file luật** JSON:

```json
{
  "rules": [
    { "when": { "call_id": "gen/com.acme.PriceCalculator" }, "respond_file": "c0_compile_error.java" },
    { "when": { "call_id_prefix": "repair/com.acme.PriceCalculator/round_1", "prompt_contains": "cannot find symbol" },
      "respond_file": "r1_assertion.java" },
    { "when": { "call_id_prefix": "repair/" }, "respond_file": "valid.java",
      "usage": { "prompt_tokens": 2000, "completion_tokens": 600 } }
  ],
  "default": { "error": "no_rule" }
}
```

- Luật khớp đầu tiên thắng.
- `respond_text` thay được cho `respond_file`.
- `error` giả lập lỗi HTTP: `429`, `401`, `500`, `timeout`.
- Không có luật nào khớp thì test fail và in rõ `call_id` cùng 200 ký tự đầu của prompt.

### 3.2 Bật

- **Trong test:** đăng ký qua DI.
- **Với file thực thi** (E2E): biến môi trường `ARROW_FAKE_LLM=<đường dẫn file luật>`. Khi biến này có giá trị, CLI in `[!] FAKE LLM` ở dòng đầu, để không thể nhầm với chạy thật.

### 3.3 Kịch bản dùng chung

`tests/scenarios/<tên>/`, mỗi kịch bản gồm file luật và các câu trả lời. Ít nhất có:

| Kịch bản | Mục đích |
|---|---|
| `valid-at-c0` | c0 VALID ngay |
| `compile-then-valid` | COMPILE → VALID ở vòng 1 |
| `o8-o3-o4-o7-o1` | Đúng ví dụ R03 §14.5 |
| `duplicate-code` | Mã trùng → lên mức |
| `regression-static` | Làm hỏng `TotalsTest` qua biến static → REGRESSION → sửa |
| `invalid-output` | Model trả về chữ không có mã → `INVALID_OUTPUT` |
| `budget-exhausted` | Hết N vòng chưa VALID |
| `llm-429-then-ok` | Gọi lại sau lỗi 429 |

## 4. Test bắt buộc theo spec

Mỗi spec Pxx có mục "Kiểm thử". Ngoài ra luôn phải có các nhóm test sau, vì đây là những chỗ sai thì gây hại:

| Nhóm | Test |
|---|---|
| An toàn với project | Mọi integration và E2E so hash thư mục project trước và sau. Chỉ được khác đúng file test mà P06 ghi khi người dùng đồng ý |
| Không ghi đè | P06 §9 |
| Không lộ khoá | Chạy E2E với khoá giả `sk-test-SECRET-123`, rồi tìm chuỗi này trong toàn bộ `ARROW_HOME` và stdout/stderr: phải không có |
| Thuật toán Adaptive | Bảng R03 §7 (phần Adaptive), hai ví dụ Q5, R03 §14–15 dưới dạng test theo bảng (`[Theory]`) |
| Kiến trúc | `ArchitectureTests`: Cli không tham chiếu DataAccess trực tiếp, trừ trong `CompositionRoot`; Business không tham chiếu Cli; không lớp nào tham chiếu ngược |

**Bộ đối chiếu từ `ARROW_Framework`:**
- JUnit test của java-helper giữ nguyên, vì java-helper được giữ nguyên.
- Test C# của khối 1–4 **không chép nguyên**. Mỗi test được viết lại theo interface mới, giữ **đầu vào và kết quả mong đợi** (log mẫu, XML mẫu, bảng ví dụ của chain sửa). Như vậy code mới phải cho kết quả giống code cũ.
- Bỏ các test chỉ dành cho thực nghiệm: Fixed, manifest, tiêu chí B, RQ, EvoSuite, màn hình console.
- Mỗi test đối chiếu ghi tên test gốc trong comment, để dò lại khi cần.

## 5. CI (GitHub Actions)

### 5.1 `ci.yml`, chạy trên mọi push và PR

```text
jobs:
  build-test (matrix: windows-latest, ubuntu-latest)
    1. checkout
    2. setup-dotnet 10.0.x (theo global.json)
    3. setup-java: temurin 8, 11, 17, 21 (đặt JAVA_HOME_8_X64…; tạo ARROW_HOME/config.json trỏ tới các JDK này)
    4. cài Maven 3.9.x và Gradle 8.x; cache ~/.m2, ~/.gradle, ~/.nuget/packages
    5. mvn -q -f tools/java-helper/pom.xml verify          (java-helper + test)
    6. dotnet build -c Release -warnaserror
    7. dotnet test --filter Category!=Integration&Category!=E2E
    8. dotnet test --filter Category=Integration
    9. (ubuntu) dotnet test --filter Category=E2E
   10. tải lên kết quả test (trx) và ARROW_HOME/logs khi fail
```

**Gộp PR khi:** cả hai hệ điều hành xanh ở bước 5–9.

### 5.2 `nightly.yml`, mỗi đêm

- E2E trên cả hai hệ điều hành;
- thêm `macos-latest` (osx-arm64) cho unit và integration.

### 5.3 `release.yml`, khi có tag `v*`

Chạy lại toàn bộ `ci.yml`, sau đó đóng gói (Mục 7) và tạo GitHub Release kèm:
- các file chạy độc lập;
- file `.nupkg`;
- `SHA256SUMS.txt`;
- ghi chú phát hành lấy từ `CHANGELOG.md`.

Đẩy lên NuGet là một bước có `environment: release`, cần nhóm duyệt bằng tay.

## 6. Kiểm chứng trên repo thật

**Bộ repo:**
- **Nguồn:** `D:\Ky9\DoAn\ARROW\preflight_manifest\*\strict_eligible_manifest.jsonl`, các repo đã được preflight xác nhận build và chạy test được.
- **Chọn:** script `scripts/make-validation-set.py`, seed 42.
  1. Gộp mọi dòng, mỗi repo giữ một CUT.
  2. Chia ô theo `build_tool × test framework`.
  3. Mỗi ô lấy theo thứ tự `sha256("42|<repo_url>")`, cho tới đủ 15 repo. Ô nào không đủ thì lấy hết.
- **Kết quả:** `docs/validation-set.json`, gồm `repo_url`, `commit`, `cut_fqn`, `build_tool`, `framework`, `java_major`. File này được commit để lần sau chạy lại đúng bộ đó.

**Chạy:**
- `scripts/validate.ps1 --model <tên>` gọi `arrow gen <cut> --repo <url> --ref <commit> --yes --json` cho từng dòng, rồi gom kết quả vào `docs/validation.md`.
- **Bảng kết quả:** repo, CUT, kết quả, c0, số vòng, thời gian từng khối, token, chi phí, các chỉ số, mã lỗi.
- **Tổng:**
  - tỷ lệ VALID;
  - tỷ lệ `BLOCKED` kèm lý do;
  - tỷ lệ INFRA;
  - trung vị thời gian và token mỗi class.
- Ca nào gặp `-Werror` thì ghi riêng, theo plan.

**Ngưỡng phát hành v1** (nhóm chốt lại ở GĐ7):
- không ca nào làm sửa project hay ghi sai chỗ;
- mọi ca `BLOCKED` hay `INFRA` đều có mã lỗi và gợi ý đúng;
- tỷ lệ VALID trên các ca không `BLOCKED` được báo cáo. Ngưỡng tối thiểu do nhóm quyết sau khi xem số liệu.

## 7. Đóng gói

| Bản | Cách tạo | Ghi chú |
|---|---|---|
| `dotnet tool` | `dotnet pack src/Arrow.Cli -c Release` với `PackAsTool=true`, `ToolCommandName=arrow`, `PackageId=<chốt tên>` | Cần .NET 10 runtime trên máy. Cài bằng `dotnet tool install -g <PackageId>` |
| Chạy độc lập | `dotnet publish src/Arrow.Cli -c Release -r <rid> --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` | `rid`: `win-x64`, `linux-x64`, `osx-arm64`. Đóng thành `arrow-<ver>-<rid>.zip` hoặc `.tar.gz` |

- **Không bật** `PublishTrimmed` hay `PublishAot`, vì System.Text.Json reflection và Spectre.Console không hỗ trợ đầy đủ.
- **Tài nguyên nhúng** (`EmbeddedResource` của `Arrow.Business` và `Arrow.DataAccess`):
  - `java-helper.jar`;
  - `tools.lock.json`;
  - template prompt;
  - `ErrorCatalog` theo ngôn ngữ.

  Lúc chạy, `java-helper.jar` được giải nén vào `ARROW_HOME/tools/java-helper-<ver>.jar` nếu chưa có hoặc sai sha256.
- **Phiên bản:**
  - SemVer, đặt trong `Directory.Build.props` (`<Version>`);
  - `arrow --version` in phiên bản, commit và ngày build;
  - `run.json` ghi phiên bản này.
- **Ký file:** v1 chưa ký mã. Windows SmartScreen có thể cảnh báo khi chạy file `.exe`, README ghi rõ điều này.

## 8. Tài liệu đi kèm bản phát hành

- `README.md`: cài đặt (2 cách) → `arrow setup` → `arrow gen` → phiên tương tác; yêu cầu hệ thống (P10, P01); quyền riêng tư khi dùng LLM.
- `docs/troubleshooting.md`: mỗi mã lỗi thường gặp một mục, gồm nguyên nhân và cách sửa, sinh từ `ErrorCatalog`.
- `CHANGELOG.md`.
- `LICENSE`: nhóm chốt.

## 9. Nghiệm thu

- CI xanh trên Windows và Ubuntu, không có test bị bỏ qua.
- Trên một máy sạch (máy ảo Windows 11 và Ubuntu 24.04, chỉ có JDK 17 và Maven): tải bản phát hành, chạy `arrow setup`, rồi `arrow gen` trên `maven-verify-junit4`, ra `VALID` với model thật.
- `docs/validation.md` đã có, và nhóm đã duyệt.
