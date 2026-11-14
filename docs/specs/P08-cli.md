# P08. Giao diện dòng lệnh (CLI)

**Mục đích:** quy định mọi thứ người dùng thấy và gõ: lệnh, tuỳ chọn, phiên tương tác, cách hiển thị, hỏi xác nhận, mã thoát, output JSON.

**Lớp:** `Arrow.Cli`. Lớp này chỉ phân tích tham số, gọi use case ở `Arrow.Business` và hiển thị kết quả. Nó không chứa luật nghiệp vụ, không chạy tiến trình, không đọc/ghi file kết quả (trừ khi gọi qua use case).

**Thư viện:** System.CommandLine 2.0.x (phân tích lệnh), Spectre.Console (hiển thị, hỏi, chọn).

## 1. Nguyên tắc

| Nguyên tắc | Cách làm |
|---|---|
| Lệnh là chính | Mọi việc đều làm được bằng một lệnh có tham số, chạy xong thì thoát. Phiên tương tác (Mục 5) chỉ là cách gọi các lệnh đó tiện hơn |
| Mặc định an toàn | Không ghi vào project nếu chưa hỏi; không tốn nhiều tiền nếu chưa hỏi (P07 §4) |
| Dùng được trong script và CI | `--yes`, `--json`, `--plain`, mã thoát rõ ràng; không bao giờ chờ bàn phím khi stdin không phải terminal |
| Lỗi có cách sửa | Mỗi lỗi in mã, câu giải thích và gợi ý (từ `ErrorCatalog`) |
| Không lộ bí mật | Khoá API chỉ hiện 4 ký tự cuối |
| Ngôn ngữ | Thông báo tiếng Việt theo `ui.language`. Tên lệnh, tuỳ chọn và trạng thái (`VALID`, `COMPILE`…) giữ tiếng Anh |

## 2. Lệnh

### 2.1 Danh sách

| Lệnh | Việc | Use case | Spec |
|---|---|---|---|
| `arrow` | Mở phiên tương tác | `InteractiveSession` | Mục 5 |
| `arrow gen <class…>` | Sinh test | `GenerateTestsUseCase` | P07 |
| `arrow inspect <class>` | Chỉ chạy khối 1: in M, build root, JDK, baseline | `InspectUseCase` | P01 |
| `arrow setup` | Thiết lập lần đầu | `SetupUseCase` | Mục 2.5 |
| `arrow doctor` | Kiểm tra môi trường | `DoctorUseCase` | Mục 2.6 |
| `arrow models [list]`, `models test [<tên>]`, `models add`, `models default <tên>` | Xem model; gọi thử một lượt; thêm model; đặt mặc định | `ModelsUseCase` | P03 §10 |
| `arrow history [--limit n]` | Danh sách run | `HistoryUseCase` | P09 §5.4 |
| `arrow show <run> [<class>]` | Chi tiết run, class, từng vòng sửa | `ShowRunUseCase` | Mục 2.7 |
| `arrow config list\|get\|set\|unset\|path` | Cấu hình | `ConfigUseCase` | P09 §3.4 |
| `arrow secrets list\|set\|remove` | Khoá API | `SecretsUseCase` | P09 §4 |
| `arrow tools install\|list` | Công cụ ngoài | `ToolsUseCase` | P09 §6 |
| `arrow clean --work\|--runs\|--mirrors` | Dọn dữ liệu | `CleanUseCase` | P09 §8 |

**Tuỳ chọn chung:**
- `--help`/`-h`, `--version`;
- `--plain`, `--json`;
- `--verbose`/`-v`;
- `--yes`/`-y`;
- `--home <dir>` (ghi đè `ARROW_HOME`).

### 2.2 `arrow gen`

```
arrow gen <class…> [--project <dir> | --repo <url> [--ref <ref>]] [--package <pkg>]
          [--model <tên>] [--rounds <N>] [--no-repair] [--no-metrics]
          [--yes] [--dry-run] [--jobs <n>] [--max-tokens <n>] [--resume <run>] [--json] [--plain]
```

| Tuỳ chọn | Mặc định | Ý nghĩa |
|---|---|---|
| `<class…>` | — | Một hoặc nhiều tên ngắn, FQN hoặc đường dẫn `.java`. Có thể bỏ trống nếu dùng `--package` |
| `--project <dir>` | thư mục hiện tại | Project trên máy |
| `--repo <url>` | — | Repo GitHub https. Không dùng cùng `--project` |
| `--ref <ref>` | nhánh mặc định | Nhánh, tag hoặc SHA. Chỉ đi với `--repo` |
| `--package <pkg>` | — | Mọi class cụ thể trong package (P01 §3.3 bước 6) |
| `--model <tên>` | `llm.default_model` | Khoá trong `llm.models` |
| `--rounds <N>` | `repair.rounds` (5) | Số vòng sửa tối đa, 1–10 |
| `--no-repair` | — | Chỉ sinh và xác minh c0 |
| `--no-metrics` | — | Bỏ khối 4 |
| `--yes` | — | Không hỏi: tự đồng ý ghi file và chấp nhận chi phí ước tính |
| `--dry-run` | — | Chạy đủ nhưng không ghi vào project; in đường dẫn file kết quả trong `runs/` |
| `--jobs <n>` | `run.jobs` (1) | Số class chạy song song, 1–8 |
| `--max-tokens <n>` | `run.max_tokens` | Ngân sách token của cả run (P03 §6) |
| `--resume <run>` | — | Chạy tiếp một run bị ngắt (P07 §5) |

**Kiểm tra tham số** trước khi chạy:
- không có class và không có `--package` → lỗi;
- `--repo` cùng `--project` → lỗi;
- `--ref` không có `--repo` → lỗi.

Lỗi tham số in ngắn kèm `Gõ arrow gen --help để xem cách dùng`, mã thoát 2.

**Trạng thái (14/11/2026, GĐ4, mốc M1):** đã có bản tối thiểu:
- **Tuỳ chọn có:** nhiều class và `--package` (từ GĐ6, 14/11/2026), `--jobs`, `--project`/`--repo`/`--ref`, `--model`, `--rounds`, `--no-repair`, `--no-metrics`, `--yes`, `--dry-run`, `--max-tokens`; ước tính chi phí và câu hỏi của P07 §4; bảng tổng khi có nhiều class.
- **Khối 4:** có từ GĐ5 (14/11/2026). Bộ cuối VALID được đo bằng JaCoCo, PIT và tsDetect, rồi in dòng `Chất lượng: line … · mutation … · smell …`. `--no-metrics` hoặc `quality.enabled = false` thì không đo.
- **Ghi file:** chỉ ghi bộ VALID, không ghi đè, chặn đường dẫn ra ngoài project.
- **Có thêm trong GĐ6 (14/11/2026):** `--resume`; một câu hỏi ghi file cho nhiều class; đổi tên rồi xác minh lại khi tên vừa xuất hiện; hiển thị theo khối như Mục 3.2, có giờ ở chế độ plain, và bảng `Live` khi chạy nhiều class ở chế độ rich. Câu hỏi luôn chờ phần hiển thị in xong rồi mới hiện.

### 2.3 `arrow inspect`

```
arrow inspect <class> [--project <dir> | --repo <url> [--ref <ref>]] [--json]
```

In ra:
- build tool và wrapper;
- build root, chế độ (reactor hoặc standalone) và bộ chọn module;
- Java khai báo, JDK đã chọn và lý do chọn;
- test framework, thư viện mock và assertion;
- số dependency;
- source root và test root;
- `generated_test_path`;
- baseline: số test pass ở cả 2 lần, flaky, fail;
- thời gian từng bước.

Không gọi LLM.

### 2.4 Lệnh nhỏ

- `arrow models test <tên>`: gửi một prompt ngắn cố định, in model id trả về (`[OK]` hoặc `[X]` so với `expected_model_id_prefix`), token, thời gian, chi phí.
- `arrow history`: bảng `run_id · thời gian · project · số class · VALID · đã ghi · trạng thái`, mới nhất trước, mặc định 20 dòng.

### 2.5 `arrow setup`

Gồm 5 bước. Mỗi bước in một dòng "Bước này làm gì", rồi hỏi. Gõ `Esc` hoặc chọn "Quay lại" để về bước trước.

1. **Nhà cung cấp LLM:**
   - chọn một trong: `OpenAI`, `OpenAI-compatible (nhập URL)`, `Anthropic`, `Ollama (trên máy)`, `OpenRouter`;
   - nhập `base_url` (có giá trị gợi ý);
   - nhập khoá, **ẩn ký tự khi gõ**; Ollama thì bỏ qua bước này.
2. **Model:** nhập `model_id`, đặt tên ngắn; có thể nhập giá (tuỳ chọn); gọi thử một lượt; đặt làm mặc định.
3. **Quyền riêng tư:** in cảnh báo rằng source của class và thông tin project sẽ gửi tới nhà cung cấp; Ollama giữ code trên máy. Hỏi `Đồng ý? (Y/n)`.
4. **Java và build:** tự tìm JDK 8, 11, 17, 21 (Mục 2.6) và Maven/Gradle, in bảng; người dùng sửa đường dẫn nếu cần.
5. **Công cụ:** chạy `arrow tools install`.

Cuối cùng: in bảng xem lại, hỏi `Lưu?`, ghi `config.json` và khoá, rồi tự chạy `arrow doctor`.

**Trạng thái (14/11/2026, GĐ6):** đủ 5 bước. Bước 2 gọi thử model **trước** khi lưu (khoá còn trong bộ nhớ), nên cấu hình sai không để lại gì trong `ARROW_HOME`. Bước 4 in bảng JDK 8/11/17/21, Maven và Gradle tìm được; người dùng chọn "Sửa JDK <n>", "Sửa Maven" hoặc "Sửa Gradle" để nhập đường dẫn, bảng được tìm lại ngay; chỉ đường dẫn người dùng sửa được ghi vào `java.*` của `config.json`. Bước 5 chỉ hỏi có tải công cụ đo không; việc tải chạy **sau** `Lưu?`, nên huỷ trước đó không để lại file nào. "Quay lại" ở bước 4 về bước 3, ở bước 5 về bước 4.

- **Không có bàn phím** (stdin không phải terminal): `setup` báo lỗi và hướng dẫn dùng `arrow config set` và `arrow secrets set --stdin`.
- **Lần đầu chạy `arrow` hay `arrow gen`** mà chưa có `config.json`: hỏi `Chưa thiết lập. Chạy arrow setup ngay? (Y/n)`.

### 2.6 `arrow doctor`

In từng dòng ngay khi kiểm xong một mục:

```
Kiểm tra môi trường
✓ ARROW_HOME       C:\Users\a\AppData\Local\arrow · 84 GB trống
✓ git              2.46.0
✓ JDK 8 / 11 / 17 / 21   1.8.0_422 / 11.0.24 / 17.0.12 / —
! JDK 21           không tìm thấy (chỉ cần cho project Java 21)
✓ Maven            3.9.9 · Gradle 8.10
✓ java-helper      1.1.0 (JDK 17)
✓ JaCoCo / PIT / tsDetect   0.8.12 / 1.17.4 / 2.2 · sha256 khớp
✓ Model gpt-x      api.teamorouter.com · gpt-4.1-mini · 0.8 s
Kết quả: SẴN SÀNG (1 cảnh báo)
```

**Cách tìm JDK:**
- `java.jdk_homes`;
- `JAVA_HOME`;
- `PATH`;
- trên Windows: registry `HKLM\SOFTWARE\JavaSoft`, `Eclipse Adoptium`, `Microsoft`, `Zulu`, thư mục `C:\Program Files\Java\*`, `C:\Program Files\Eclipse Adoptium\*`;
- trên macOS: `/usr/libexec/java_home -V`;
- trên Linux: `/usr/lib/jvm/*`;
- SDKMAN `~/.sdkman/candidates/java/*`.

Phiên bản lấy từ `java -version`.

**Mức kết quả của từng mục:**
- `✓` đạt;
- `!` cảnh báo, vẫn chạy được;
- `✗` lỗi, kèm dòng `→ Cách sửa: …`.

**Kết luận:** `SẴN SÀNG`, `SẴN SÀNG (n cảnh báo)` hoặc `CHƯA SẴN SÀNG (n lỗi)`. Mã thoát 0 nếu không có lỗi, 2 nếu có.

**Kiểm tra thêm:**
- Ollama: `num_ctx` ≥ 40.960 (P03);
- Windows: hỗ trợ đường dẫn dài (`LongPathsEnabled`), nếu tắt thì cảnh báo.

### 2.7 `arrow show`

- **`arrow show <run>`:** thông tin run và bảng mỗi CUT một dòng:
  `Class · c0 · kết quả · vòng · line · branch · mutation · smell · token · file đã ghi`.
- **`arrow show <run> <class>`:** chi tiết một CUT:
  - khối 1 tóm tắt;
  - c0;
  - bảng từng vòng sửa: `Vòng · Mức · Kết quả · So với checkpoint · Quyết định`;
  - chỉ số khối 4;
  - đường dẫn các file trong `runs/`.

```
Vòng  Mức  Kết quả                                 So với checkpoint  Quyết định
c0    -    COMPILE · cannot find symbol | method   -                  checkpoint = c0
1     1    COMPILE · constructor cannot be applied ngang              giữ mức
2     1    mã trùng                                -                  lên mức 2
3     2    ASSERTION 4/5                           tốt hơn            checkpoint = 3, về mức 1
4     1    VALID                                   -                  thành công
```

## 3. Hiển thị khi chạy `gen`

### 3.1 Chế độ

| Chế độ | Khi nào | Cách hiển thị |
|---|---|---|
| `rich` | stdout là terminal, không có `NO_COLOR`, `ui.color` ≠ `never`, không có `--plain` | Màu, ký hiệu `✓ ✗ ▶ ! ·`, spinner cho bước đang chạy, bảng có khung |
| `plain` | Ngược lại, hoặc khi có `--plain` | Không màu, không spinner, ký hiệu ASCII `[OK] [X] [>] [!]`, mỗi sự kiện một dòng có giờ ở đầu |
| `json` | `--json` | stdout **chỉ** có một đối tượng JSON khi kết thúc (Mục 7). Tiến độ in dạng `plain` ra **stderr** |

Trên Windows, khi khởi động: đặt `Console.OutputEncoding = UTF8` và bật Virtual Terminal Processing. Nếu không bật được thì tự chuyển sang `plain`.

### 3.2 Nội dung

`Arrow.Cli` nhận `ArrowEvent` (P07 §10) rồi hiển thị. Ví dụ ở chế độ `rich`, chạy một class:

```
ARROW · OrderService (com.myshop.service) · gpt-x · N=5
✓ Khối 1  Maven · build root . · JDK 17 · JUnit 5 + Mockito · baseline 42 test ×2 · 38 s
✓ Khối 2  c0 · 1.9k token · 6.2 s
▶ Khối 3  c0: COMPILE · cannot find symbol | method
          vòng 1 · mức 1 → COMPILE (signature khác, giữ mức)        14 s
          vòng 2 · mức 1 → mã trùng, lên mức 2
          vòng 3 · mức 2 → ASSERTION 4/5 · tốt hơn · checkpoint     19 s
          vòng 4 · mức 1 → VALID ✓                                   17 s
✓ Khối 4  line 78% · branch 64% · mutation 61% · smell 0.50 · 2 phút 10 s
? Ghi src/test/java/com/myshop/service/OrderService_ArrowTest.java (6 test)? (Y/n)
✓ Đã ghi. Tổng: 3 phút 52 s · 4 lượt gọi model · 12.4k token · 0.006 USD
```

**Chạy nhiều class:**
- mỗi class một khối như trên, có thứ tự `[2/5]`;
- khi chạy song song (`--jobs`), dùng bảng tiến độ trực tiếp (Spectre `Live`), mỗi class một dòng;
- kết thúc in bảng tổng như `arrow show <run>`, sau đó hỏi ghi file (Mục 4.2).

**Lỗi khiến dừng** (ví dụ `BLOCKED`):

```
✗ Khối 1  B1_BASELINE_BUILD_FAILED · Không biên dịch được project với JDK 17, 8, 11, 21
  Lỗi đầu tiên: [ERROR] … cannot find symbol: class Generated (javax.annotation)
  → Cách sửa: chạy "mvn -q test-compile" trong project để xem lỗi đầy đủ; project có thể cần JDK 8.
  Log: C:\…\runs\20261110-142530-k3f9\targets\com.myshop.OrderService\block1\logs\compile.log
```

## 4. Hỏi người dùng

### 4.1 Khi nào hỏi

| Tình huống | Câu hỏi | Mặc định | Bỏ qua khi |
|---|---|---|---|
| Nhiều class trùng tên | Chọn FQN (danh sách) | — | Không bỏ được: dùng FQN. Không có bàn phím hay `--yes` thì lỗi `B1_CUT_AMBIGUOUS` |
| Chi phí ước tính (P07 §4) | `Ước tính ~45k token (~0.03 USD) cho 8 class. Tiếp tục? (Y/n)` | Y | `--yes` |
| Ghi file test | `Ghi <đường dẫn> (n test)? (Y/n)` | Y | `--yes`; `--dry-run` thì không hỏi và không ghi |
| Có run dở dang | `Run 20261110-1425 chưa xong (3/8 class). Chạy tiếp? (Y/n)` | Y | `--resume`, `--yes` (= chạy tiếp) |
| Chưa thiết lập | `Chạy arrow setup ngay? (Y/n)` | Y | Không có bàn phím thì lỗi `CFG_NO_MODEL` |

### 4.2 Ghi file khi chạy nhiều class

Sau bảng tổng, hỏi **một lần** với danh sách chọn nhiều (mọi class VALID được chọn sẵn): `Chọn file test để ghi vào project`.

### 4.3 Không có bàn phím

`Console.IsInputRedirected` hoặc biến `CI=true`:
- không hỏi gì;
- câu hỏi có `--yes` thì coi như đồng ý;
- không có `--yes` thì coi như **không đồng ý** với việc ghi file và chi phí, và in `[!] Bỏ qua: cần --yes khi chạy không tương tác`.

## 5. Phiên tương tác (`arrow`)

### 5.1 Mở phiên

1. Tìm project: đi lên từ thư mục hiện tại để tìm file build gần nhất. Không thấy thì phiên vẫn mở, banner ghi `chưa chọn project`, và `/project <dir>` để chọn.
2. **Banner:**

```
 ARROW · LLM unit test generation for Java · v1.0.0
 ╭─────────────────────────────────────────────────────────────╮
 │ project  MyShop  (Maven · Java 17 · JUnit 5)                │
 │ model    gpt-x · api.teamorouter.com · repair adaptive N=5   │
 ╰─────────────────────────────────────────────────────────────╯
 Gõ /help để xem lệnh · Tab để gợi ý · Ctrl+C để huỷ việc đang chạy
arrow>
```

Thông tin build tool, Java và test framework trong banner lấy bằng phân tích nhanh: chỉ đọc file build, không biên dịch. Phân tích đầy đủ chỉ chạy khi gọi `/gen`.

### 5.2 Lệnh trong phiên

| Lệnh | Tương đương |
|---|---|
| `/gen [class…] [tuỳ chọn]` | `arrow gen …` với `--project` = project của phiên. Không có class thì mở ô chọn class (Mục 5.3) |
| `/inspect <class>` | `arrow inspect` |
| `/history`, `/show <run> [class]` | như lệnh tương ứng |
| `/model [tên]` | Không có tên: liệt kê model và đánh dấu model đang dùng. Có tên: đổi model **cho phiên này** |
| `/project <dir>` | Đổi project của phiên |
| `/setup`, `/doctor`, `/config …`, `/tools …` | như lệnh tương ứng |
| `/help`, `/exit` (hoặc Ctrl+D) | |

- Gõ không có `/` ở đầu thì coi là dòng lệnh của `arrow`, ví dụ `gen OrderService --no-metrics`.
- Gõ `/` thì hiện danh sách lệnh để chọn. Tab gợi ý tên lệnh, tên tuỳ chọn và tên class.
- Lịch sử lệnh (phím lên/xuống) lưu ở `ARROW_HOME/repl_history`, tối đa 500 dòng.

### 5.3 Ô chọn class

- Danh sách lấy bằng java-helper `list_classes` trên source root của mọi module, lưu tạm trong phiên.
- Gõ để lọc theo tên ngắn hoặc một phần FQN, không phân biệt hoa thường. Interface, annotation và class abstract không hiện.
  - Danh sách chọn nhiều của Spectre.Console 0.57 không có ô tìm kiếm, nên ô chọn hỏi chuỗi lọc trước (Enter để xem tất cả), rồi mới hiện danh sách đã lọc.
- Class đã có `<CUT>Test` hay `<CUT>_ArrowTest` được ghi chú `(đã có test)`.
- Chọn nhiều bằng phím cách, Enter để chạy.

### 5.4 Huỷ

- **Ctrl+C khi đang chạy:** huỷ lần chạy hiện tại (P07 §8; phần đã xong được giữ để chạy tiếp, P07 §5), in `[!] Đã huỷ`, rồi về dấu nhắc.
- **Ctrl+C hai lần trong 2 giây,** hoặc Ctrl+C ở dấu nhắc trống: thoát phiên.

## 6. Nhập bí mật

- Dùng `AnsiConsole.Prompt(new TextPrompt<string>(…).Secret())`, hiện `*` cho mỗi ký tự.
- Không có bàn phím thì không hỏi (Mục 4.3).
- Sau khi nhập, chỉ in `[OK] Đã lưu khoá …9f1c`.

## 7. Output JSON (`--json`)

`arrow gen --json` in đúng một đối tượng ra stdout khi kết thúc:

```json
{
  "run_id": "20261110-142530-k3f9",
  "status": "COMPLETED",
  "exit_code": 1,
  "targets": [
    { "cut_fqn": "com.myshop.OrderService", "result": "VALID", "c0_state": "COMPILE", "rounds": 4,
      "test_file": "src/test/java/com/myshop/OrderService_ArrowTest.java", "written": true,
      "metrics": { "line": 0.78, "branch": 0.64, "method": 0.86, "mutation": 0.61, "smell_density": 0.5 },
      "tokens": { "in": 9800, "out": 2600 }, "cost_usd": 0.006, "seconds": 232.1, "error": null },
    { "cut_fqn": "com.myshop.Cart", "result": "ASSERTION", "rounds": 5, "written": false,
      "error": { "code": null, "message": "Hết 5 vòng, còn 1/7 test fail" } }
  ],
  "totals": { "llm_calls": 9, "tokens_in": 21000, "tokens_out": 5400, "cost_usd": 0.013, "seconds": 410.0 }
}
```

- Giá trị `result` là: `VALID`, `COMPILE`, `UNKNOWN`, `DISCOVERY`, `RUNTIME`, `ASSERTION`, `REGRESSION`, `INVALID_OUTPUT`, `INFRA`, `BLOCKED`, `CANCELLED`.
- Các lệnh `inspect`, `doctor`, `history`, `show`, `models`, `config list` cũng hỗ trợ `--json`, mỗi lệnh có schema riêng. Schema đặt trong `docs/json/*.schema.json`, tạo bằng code và được kiểm tra trong test.

## 8. Mã thoát

| Mã | Ý nghĩa |
|---|---|
| 0 | Thành công. `gen`: mọi class đều có test VALID (đã ghi hoặc bỏ qua theo lựa chọn). Lệnh khác: chạy xong |
| 1 | `gen`: có ít nhất một class không đạt VALID (`INVALID_OUTPUT`, trạng thái lỗi, `INFRA`) |
| 2 | Lỗi tham số, cấu hình hoặc môi trường; `BLOCKED` ở mọi class; run dừng vì lỗi LLM (`B2_LLM_CONFIG`, `B2_WRONG_MODEL`, `B2_TOKEN_BUDGET`, P07 §8); `doctor` có lỗi |
| 3 | Người dùng huỷ (Ctrl+C), hoặc từ chối tiếp tục khi được hỏi chi phí |
| 4 | Lỗi không lường trước (exception). In `[X] Lỗi nội bộ`, đường dẫn log, và gợi ý báo lỗi kèm file log |

## 9. Cấu trúc code

```text
Arrow.Cli/
  Program.cs                 tạo RootCommand, CompositionRoot, chạy; bắt exception cuối cùng → mã 4
  CompositionRoot.cs         DI: đăng ký DataAccess, Business, Renderer
  Commands/                  GenCommand, InspectCommand, SetupCommand, DoctorCommand, ModelsCommand,
                             HistoryCommand, ShowCommand, ConfigCommand, SecretsCommand, ToolsCommand, CleanCommand
  Interactive/               InteractiveSession, SlashCommandParser, ClassPicker, LineEditor
  Rendering/                 IRenderer, RichRenderer, PlainRenderer, JsonResultWriter, EventFormatter, Tables
  Prompts/                   IUserPrompter, SpectrePrompter, NonInteractivePrompter
```

- Mỗi `*Command` chỉ làm 3 việc: đọc tham số thành một request record, gọi use case, chuyển kết quả cho `IRenderer`.
- `IUserPrompter` được truyền xuống use case qua interface `IConfirmation` (định nghĩa trong Business), để use case hỏi người dùng mà không phụ thuộc vào Spectre.

## 10. Mã lỗi

| Mã | Khi nào |
|---|---|
| `CLI_ARGS` | Tham số sai hoặc thiếu (mã thoát 2) |
| `CLI_NO_TTY` | Lệnh cần bàn phím (`setup`) mà stdin bị chuyển hướng |
| `CLI_INTERNAL` | Exception không lường trước (mã thoát 4) |

Các mã khác đến từ use case (P00 §6).

## 11. Kiểm thử

**Unit:**
- Phân tích tham số mọi lệnh, gồm các tổ hợp lỗi ở Mục 2.2.
- Chọn chế độ hiển thị theo terminal, `NO_COLOR`, `--plain`, `--json`.
- `NonInteractivePrompter` theo Mục 4.3.
- Ánh xạ kết quả sang mã thoát (Mục 8).
- `SlashCommandParser`.
- Lọc trong ô chọn class.

**Snapshot test:**
- Output `plain` và `json` của `gen`, `inspect`, `doctor`, `show`, chạy với dữ liệu giả (use case giả).
- File mẫu nằm trong `tests/Arrow.Cli.Tests/Snapshots/`. Đổi output thì phải cập nhật snapshot trong cùng PR.

**E2E** (`Arrow.IntegrationTests`): chạy file thực thi thật bằng `Process`, dùng `FakeLlmClient` qua biến `ARROW_FAKE_LLM=<file luật>`:
- `arrow gen <class> --yes --json` trên fixture: mã thoát 0, có file test;
- cùng lệnh không có `--yes`, stdin bị chuyển hướng: mã thoát 0 và **không** ghi file;
- stdout của `--json` parse được, không lẫn chữ khác.

**Chạy tay:** phiên tương tác trên Windows Terminal, PowerShell cũ (conhost) và terminal của VS Code; kiểm tra màu, ký hiệu và Ctrl+C.

**Nghiệm thu:**
- Mọi lệnh có `--help` đầy đủ, ví dụ cuối phần help.
- Chạy `arrow gen` trong CI với `--yes --json --plain` không treo.
