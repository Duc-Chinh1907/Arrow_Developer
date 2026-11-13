# P09. Cấu hình, khoá API, lưu trữ và công cụ ngoài

**Mục đích:** quy định mọi thứ ARROW đọc và ghi trên máy người dùng: thư mục dữ liệu `ARROW_HOME`, cấu hình phân tầng, khoá API, kết quả của từng lần chạy, cache và các công cụ ngoài (java-helper, JaCoCo, PIT, tsDetect).

**Lớp:** `Arrow.DataAccess` (đọc/ghi), `Arrow.Common` (record cấu hình), `Arrow.Business` (kiểm tra và gộp cấu hình).

**Nguồn gốc:**
- thay `experiments/*.json`, `machine.local.json` và `litellm.config.local.yaml` của R00 §3;
- cấu trúc thư mục kết quả dựa trên R00 §4;
- danh mục công cụ dựa trên `tools.lock.json` của `ARROW_Framework` và R04 §1.

## 1. Phạm vi

| Có | Không |
|---|---|
| `ARROW_HOME` và cấu trúc bên trong | Cấu hình thực nghiệm, manifest, shard |
| Cấu hình 5 tầng, lệnh `arrow config` | Đồng bộ cấu hình giữa các máy |
| Khoá API: lưu mã hoá, đọc, xoá | Đăng nhập OAuth, token GitHub |
| Thư mục `runs/<run_id>/`, `run.json`, lịch sử | Báo cáo tổng hợp nhiều run |
| Cache LLM theo run | Cache dùng chung giữa các run |
| `tools.lock.json`, `arrow tools install` | EvoSuite |
| Dọn dữ liệu cũ (`arrow clean`) | |

## 2. `ARROW_HOME`

**Vị trí**, lấy giá trị đầu tiên có:
1. biến môi trường `ARROW_HOME`;
2. Windows: `%LOCALAPPDATA%\arrow`;
3. Linux/macOS: `$XDG_DATA_HOME/arrow` nếu có, nếu không thì `~/.arrow`.

**Điều kiện:**
- đường dẫn không có dấu cách hay ký tự không phải ASCII, vì Maven và Gradle trên Windows dễ lỗi với các ký tự này (R01 §3 bước 5);
- dùng đường dẫn thật, đã giải symlink;
- vi phạm thì `arrow doctor` báo lỗi `CFG_HOME_PATH` và gợi ý đặt `ARROW_HOME`, ví dụ `D:\arrow`.

**Cấu trúc:**

```text
ARROW_HOME/
  config.json                    cấu hình người dùng (Mục 3)
  secrets/                       khoá API đã mã hoá (Mục 4), quyền chỉ chủ sở hữu
  tools/                         công cụ ngoài đã tải (Mục 6)
    tools.installed.json
  mirrors/<host>/<owner>/<repo>.git   bản sao git dùng chung (P01)
  work/<run_id>/                 workspace tạm: base, snapshot, bản sao xác minh; xoá khi run xong
  runs/<run_id>/                 kết quả từng lần chạy (Mục 5)
  logs/arrow-<yyyyMMdd>.log      log của công cụ (Mục 7)
  history.jsonl                  mỗi run một dòng (Mục 5.4)
  repl_history                   lịch sử lệnh của phiên tương tác, tối đa 500 dòng (P08 §5.2)
```

## 3. Cấu hình

### 3.1 Năm tầng

Tầng sau ghi đè tầng trước, theo **từng khoá**, không thay cả đối tượng:

| # | Tầng | Nơi | Ai sửa |
|---|---|---|---|
| 1 | Mặc định | Nhúng trong `Arrow.Common` (`ArrowDefaults`) | Nhà phát triển |
| 2 | Người dùng | `ARROW_HOME/config.json` | `arrow setup`, `arrow config set` |
| 3 | Project | `<project>/.arrow.json` | Người dùng tự tạo, có thể commit vào repo của họ |
| 4 | Biến môi trường | `ARROW_<KHOÁ>`, viết HOA, dấu `.` đổi thành `__`; ví dụ `ARROW_REPAIR__ROUNDS=3` | Người dùng, CI |
| 5 | Dòng lệnh | `--rounds 3`, `--model x`, … | Người dùng |

- Tầng 3 **không được** chứa `llm.providers.*.api_key`. Gặp thì báo lỗi `CFG_SECRET_IN_PROJECT`, không đọc khoá đó.
- Đường dẫn tương đối ở tầng 2 tính từ `ARROW_HOME`; ở tầng 3 tính từ gốc project. Các khoá đường dẫn: `java.jdk_homes.*`, `java.maven_path`, `java.gradle_path`, `java.git_path`, `prompts.override_dir`.
- **Khoá phải có trong bảng Mục 3.2**, hoặc nằm trong một map do người dùng đặt tên (`llm.models.*`, `llm.providers.*`, `java.jdk_homes.*`). Khoá lạ ở bất kỳ tầng nào đều là `CFG_INVALID`, để lỗi gõ sai tên khoá không bị bỏ qua.
- **Tầng 4:** chỉ đọc các biến `ARROW_*` có `__`; `ARROW_HOME`, `ARROW_SECRET_*`, `ARROW_MAVEN_MIRROR` không phải khoá cấu hình.
- **Giá trị dạng chuỗi** (tầng 4, tầng 5, `config set`) được đọc theo kiểu của khoá trong bảng mặc định: `null` luôn là null; `true`/`false`; số; danh sách nhận JSON (`["jacoco"]`) hoặc `jacoco,pit`; đối tượng phải là JSON. Khoá có mặc định `null` và khoá trong map nhận số hoặc `true`/`false` nếu chuỗi là giá trị JSON đó, còn lại là chuỗi.

### 3.2 Các khoá

```json
{
  "schema_version": 1,
  "llm": {
    "default_model": "gpt-x",
    "models": {
      "gpt-x": {
        "provider": "teamo",
        "model_id": "gpt-4.1-mini",
        "params": { "temperature": 0, "max_tokens": 16384 },
        "price_usd_per_million": { "input": 0.40, "output": 1.60 },
        "expected_model_id_prefix": "gpt-4.1-mini"
      }
    },
    "providers": {
      "teamo": { "kind": "openai-compatible", "base_url": "https://api.teamorouter.com/v1", "secret_ref": "teamo" },
      "local":  { "kind": "openai-compatible", "base_url": "http://localhost:11434/v1", "secret_ref": null }
    },
    "max_concurrent": 2,
    "rpm_limit": 60,
    "request_timeout_seconds": 300
  },
  "java": {
    "jdk_homes": { "8": "C:\\Java\\jdk8", "11": "…", "17": "…", "21": "…" },
    "maven_path": null,
    "gradle_path": null,
    "git_path": null
  },
  "repair": { "enabled": true, "rounds": 5, "early_stop_p": 3 },
  "verification": { "baseline_runs": 2, "step_timeout_seconds": 900, "infra_attempts": 3, "infra_wait_seconds": 30 },
  "quality": { "enabled": true, "tools": ["jacoco", "pit", "tsdetect"], "pit_timeout_seconds": 1800, "tsdetect_timeout_seconds": 300 },
  "apply": { "confirm": true },
  "run": { "jobs": 1, "max_tokens": null, "confirm_cost_from_classes": 5, "keep_workspaces": false },
  "ui": { "color": "auto", "language": "vi" },
  "prompts": { "override_dir": null }
}
```

| Khoá | Kiểu | Mặc định | Ý nghĩa |
|---|---|---|---|
| `llm.default_model` | string | `null` | Model dùng khi không có `--model`. `null` thì `gen` báo `CFG_NO_MODEL` |
| `llm.models.<tên>` | object | — | `provider` (khoá trong `llm.providers`), `model_id` (tên model ở provider), `params`, `price_usd_per_million` (tuỳ chọn), `expected_model_id_prefix` (tuỳ chọn, P03) |
| `llm.providers.<tên>` | object | — | `kind` (`openai-compatible` \| `anthropic`), `base_url`, `secret_ref` (tên khoá trong `secrets/`, `null` nếu không cần khoá) |
| `llm.max_concurrent` | int 1–8 | 2 | Số lượt gọi model cùng lúc |
| `llm.rpm_limit` | int | 60 | Số lượt mỗi phút |
| `llm.request_timeout_seconds` | int | 300 | Timeout của một lượt gọi |
| `java.jdk_homes` | map | `{}` | JDK theo số major. `arrow setup` tự điền |
| `java.maven_path`, `gradle_path`, `git_path` | string? | `null` | `null` = tìm trong `PATH` |
| `repair.enabled` | bool | true | `false` giống `--no-repair` |
| `repair.rounds` | int 1–10 | 5 | N |
| `repair.early_stop_p` | int? ≥ 3 | 3 | `null` = không dừng sớm. Đặt nhỏ hơn 3 thì báo `CFG_INVALID` (P04) |
| `verification.*` | | như trên | P01, P04 |
| `quality.enabled` | bool | true | `false` giống `--no-metrics` |
| `quality.tools` | list | cả ba | Bỏ bớt công cụ |
| `apply.confirm` | bool | true | `false` giống `--yes` |
| `run.jobs` | int 1–8 | 1 | Số CUT chạy song song |
| `run.max_tokens` | int? | `null` | Ngân sách token của một run |
| `run.confirm_cost_from_classes` | int | 5 | Từ số class này trở lên thì hỏi trước khi chạy |
| `run.keep_workspaces` | bool | false | Giữ `work/<run_id>/` để gỡ lỗi |
| `ui.color` | `auto` \| `always` \| `never` | `auto` | P08 |
| `ui.language` | `vi` \| `en` | `vi` | Ngôn ngữ của thông báo |
| `prompts.override_dir` | string? | `null` | Thư mục template thay cho bản nhúng (P02) |

### 3.3 Đọc và kiểm tra

1. Đọc 5 tầng rồi gộp thành `EffectiveConfig` (record bất biến), kèm **nguồn của từng khoá** để `arrow config list --origin` in ra.
2. **Kiểm tra:**
   - kiểu và khoảng giá trị;
   - provider được tham chiếu có tồn tại;
   - `secret_ref` có khoá trong `secrets/` hoặc trong biến môi trường (Mục 4.3). Việc này **không** làm ở bước đọc cấu hình mà ở P07 §2 bước 3, và chỉ cho provider của model đang dùng, với mã `CFG_SECRET_MISSING`; nhờ vậy một provider không dùng tới mà thiếu khoá không chặn mọi lệnh.

   Lỗi thì báo `CFG_INVALID` kèm khoá và nguồn; với `gen` thì dừng trước khi làm gì khác. Kiểm tra kiểu nằm trong `UserConfigStore` (DataAccess), kiểm tra khoảng giá trị và tham chiếu nằm trong `ConfigValidator` (Business).
3. **`schema_version` lớn hơn bản đang chạy:** báo `CFG_NEWER_SCHEMA`, không ghi đè. **Nhỏ hơn:** tự nâng cấp trong bộ nhớ, chỉ ghi lại khi người dùng chạy `config set` hoặc `setup`.
4. **File JSON hỏng:** báo `CFG_PARSE` kèm dòng và cột. Không bao giờ tự xoá file của người dùng.

### 3.4 `arrow config`

| Lệnh | Việc |
|---|---|
| `arrow config list [--origin]` | In cấu hình hiệu lực; khoá API luôn che thành `...xxxx` |
| `arrow config get <khoá>` | In một giá trị |
| `arrow config set <khoá> <giá trị> [--project]` | Ghi vào tầng 2, hoặc tầng 3 nếu có `--project`. Giá trị được parse theo kiểu của khoá |
| `arrow config unset <khoá> [--project]` | Xoá khoá khỏi tầng đó |
| `arrow config path` | In đường dẫn `config.json` và `.arrow.json` |

Ghi file theo kiểu nguyên tử (P00 §5), giữ nguyên các khoá khác và thứ tự khoá.

## 4. Khoá API

### 4.1 Lưu

| Hệ điều hành | Cách lưu |
|---|---|
| Windows | `secrets/<ref>.bin` = DPAPI `ProtectedData.Protect` (phạm vi `CurrentUser`, entropy cố định của ARROW) |
| Linux, macOS | `secrets/<ref>.key`, quyền `600`; thư mục `secrets/` quyền `700`. v1 không mã hoá thêm. Sau v1 có thể dùng Keychain hoặc libsecret |

Tên `ref` chỉ gồm `a-z`, `0-9` và `-` (tối đa 64 ký tự), để nó là tên file an toàn và chuyển một-một sang biến môi trường (`my-proxy` ↔ `ARROW_SECRET_MY_PROXY`).

### 4.2 Nhập

- Khoá được nhập trong `arrow setup` hoặc bằng `arrow secrets set <ref>`, **ẩn ký tự khi gõ** (P08 §6).
- Có thể đọc từ stdin cho script: `echo $KEY | arrow secrets set teamo --stdin`.
- **Không có** tuỳ chọn truyền khoá qua tham số dòng lệnh, vì nó sẽ nằm trong lịch sử shell.

### 4.3 Đọc

Thứ tự ưu tiên:
1. biến môi trường `ARROW_SECRET_<REF>`, viết HOA, ví dụ `ARROW_SECRET_TEAMO`;
2. biến chuẩn của provider khi `ref` trùng tên: `OPENAI_API_KEY` cho `openai`, `ANTHROPIC_API_KEY` cho `anthropic`;
3. file trong `secrets/`.

### 4.4 Lệnh

- `arrow secrets list`: in tên ref và 4 ký tự cuối của khoá.
- `arrow secrets set <ref>`, `arrow secrets remove <ref>`.

### 4.5 Bảo vệ

- `SecretValue` là kiểu riêng, `ToString()` trả `***`.
- Chỉ `ILlmClient` được gọi `Reveal()`.
- **`Redactor`** lọc mọi chuỗi trước khi ghi log hay file kết quả: thay mọi khoá đang nạp thành `***`, và che giá trị của header `Authorization`, `x-api-key`. Nó cũng che các giá trị trông giống khoá dù chưa nạp: `sk-…`, `Bearer …`, `api_key: …`.
- Khi **hiển thị** (`secrets list`, `config list`): `...` cộng 4 ký tự cuối; khoá dài không quá 8 ký tự thì chỉ hiện `***`.

## 5. Kết quả của một lần chạy

### 5.1 Định danh

`run_id` = `<yyyyMMdd-HHmmss>-<4 ký tự [a-z0-9] ngẫu nhiên>`, theo giờ địa phương, ví dụ `20261110-142530-k3f9`. `arrow show` chấp nhận cả tiền tố duy nhất, ví dụ `arrow show 20261110-1425`.

### 5.2 Cấu trúc

```text
runs/<run_id>/
  run.json
  events.jsonl                     sự kiện tiến độ (P07 §10)
  llm_cache/<sha256>.json
  targets/<cut_fqn>/
    block1/   metadata.json  metadata_prompt.txt  baseline.json  workspace.json
              dependencies_resolved.txt  test_classpath.txt  outcome.json  logs/
    block2/   prompt.json  response.json  usage.json  candidate.java  candidate.json
    block3/   verify_c0.json
              c0/  logs/  reports/                      log và báo cáo của lần xác minh c0
              round_<k>/  request.json  prompt.json  response.json  usage.json  candidate.java  candidate.json  verify.json  decision.json  logs/
              final.json  final.java
    block4/   jacoco.json  pit.json  tsdetect.json  outcome.json  logs/
    apply.json
    result.json
```

- Khối 1 dùng chung theo module (P01 §7). **Mỗi CUT luôn có** `metadata.json`, `metadata_prompt.txt` và `outcome.json` riêng, vì phần `cut` và `paths` khác nhau. Các file **dùng chung** (`baseline.json`, `workspace.json`, `dependencies_resolved.txt`, `test_classpath.txt`, `logs/`) chỉ ghi ở CUT đầu tiên của module. Các CUT sau có `block1/shared_from.txt`, chứa FQN của CUT đầu tiên đó.
- `cut_fqn` dùng làm tên thư mục, giữ nguyên dấu `.`. Class lồng không là CUT, nên không có `$`.

### 5.3 `run.json`

```json
{
  "run_id": "20261110-142530-k3f9",
  "arrow_version": "1.0.0",
  "started_at": "2026-11-10T07:25:30Z", "finished_at": "2026-11-10T07:41:02Z",
  "status": "COMPLETED",
  "command": ["gen", "OrderService", "--rounds", "5"],
  "project": { "kind": "local", "path_name": "MyShop", "root": "D:/src/MyShop", "url": null, "git_ref": null,
               "git_commit": "ab12…", "dirty": true, "source_tree_hash": "…" },
  "config": { "model": "gpt-x", "provider_host": "api.teamorouter.com", "rounds": 5, "early_stop_p": 3, "repair": true, "quality": true },
  "prompts_sha256": { "repair/user/level1.md": "…", "…": "…" },
  "tools": { "java_helper": "sha256:…", "jacoco": "0.8.12", "pit": "1.17.4", "tsdetect": "2.2" },
  "targets": [ { "cut_fqn": "com.myshop.OrderService", "state": "DONE" } ],
  "totals": { "llm_calls": 6, "tokens_in": 24310, "tokens_out": 7010, "cost_usd": 0.0210 }
}
```

- `project.kind` là `local` hoặc `git`. Project từ GitHub có `url` (không kèm thông tin đăng nhập) và `git_ref`, còn `root` và `source_tree_hash` là `null`.
- `source_tree_hash` và `config.repair` dùng để kiểm tra khi chạy tiếp (P07 §5.2).
- `status` là `RUNNING`, `COMPLETED`, `CANCELLED` hoặc `FAILED`.
- `targets[].state` là `PENDING`, `RUNNING` hoặc `DONE`. Kết quả của target (`VALID`, `BLOCKED`…) nằm trong `result.json` (P07 §5.3, §6).
- `run.json` được ghi lại (nguyên tử) mỗi khi một target đổi trạng thái.
- **Không bao giờ** chứa khoá API hay `base_url` đầy đủ, chỉ có tên máy chủ.

### 5.4 Lịch sử

`ARROW_HOME/history.jsonl`: khi run kết thúc, thêm một dòng `{run_id, started_at, project_name, cuts, valid, written, status}`.
- `arrow history` đọc file này (P08).
- Dòng hỏng thì bỏ qua và in cảnh báo.

### 5.5 `result.json` của một CUT

Định nghĩa ở P07 §6. `arrow show` và `--json` dùng file này.

## 6. Công cụ ngoài

### 6.1 `tools.lock.json`

Nhúng trong bản phát hành. Lấy từ `ARROW_Framework/tools/tools.lock.json`, **bỏ hai mục EvoSuite**, thêm mục java-helper:

| id | Phiên bản | Nguồn |
|---|---|---|
| `java-helper` | theo bản phát hành | nhúng sẵn, giải nén vào `tools/java-helper-<ver>.jar` |
| `jacoco-agent`, `jacoco-cli` | 0.8.12 | Maven Central |
| `pitest`, `pitest-entry`, `pitest-command-line`, `pitest-html-report` | 1.17.4 | Maven Central |
| `commons-text` 1.12.0, `commons-lang3` 3.14.0, `jopt-simple` 4.9 | | Maven Central (PIT cần) |
| `pitest-junit5-plugin` | 1.2.1 | Maven Central |
| `pitest-testng-plugin` | 1.0.0 | Maven Central |
| `tsdetect` | 2.2 | GitHub Releases `TestSmells/TestSmellDetector` |

Mỗi mục có `sha256` cố định, lấy từ file của nhóm.

### 6.2 `arrow tools install`

1. Với mỗi mục chưa có, hoặc có nhưng sai sha256: tải về `tools/<file>.part`, kiểm tra sha256, rồi đổi tên.
   - Mục Maven Central tải thẳng qua HTTPS từ `https://repo1.maven.org/maven2/<group path>/<artifact>/<ver>/<artifact>-<ver>[-<classifier>].jar`. Không cần Maven trên máy.
   - Có biến `ARROW_MAVEN_MIRROR` thì dùng URL đó thay `repo1.maven.org`.
2. Sai sha256 sau khi tải: xoá file, báo `CFG_TOOL_CHECKSUM`, không dùng.
3. Ghi `tools/tools.installed.json`: `{id, file, sha256, installed_at}`.
4. `arrow gen` cần công cụ mà chưa có: tự chạy bước 1 cho đúng công cụ đó, kèm dòng tiến độ. Không có mạng thì báo `CFG_TOOL_MISSING` và hướng dẫn chạy `arrow tools install` khi có mạng; khối 4 bị bỏ qua, các khối khác vẫn chạy.

## 7. Log của công cụ

- `ARROW_HOME/logs/arrow-<yyyyMMdd>.log`: mọi lệnh, cảnh báo, lỗi, ngoại lệ kèm stack trace. Giữ 14 ngày.
- `--verbose` in thêm log mức Debug ra stderr.
- Log của từng lệnh build hay công cụ nằm trong thư mục `logs/` của khối tương ứng, theo định dạng ở P00 §5.

## 8. Dọn dẹp

| Lệnh | Việc |
|---|---|
| `arrow clean --work` | Xoá `work/`, gồm các workspace sót lại do bị ngắt |
| `arrow clean --runs --older-than 30d` | Xoá run cũ và dòng tương ứng trong `history.jsonl` |
| `arrow clean --mirrors` | Xoá mirror git |

- Lệnh nào cũng hỏi xác nhận, `--yes` để bỏ qua. In dung lượng đã giải phóng.
- Khi khởi động, `arrow gen` xoá các `work/<run_id>/` có `run.json` không ở trạng thái `RUNNING`, hoặc của tiến trình không còn sống.

## 9. Mã lỗi

| Mã | Khi nào | Gợi ý |
|---|---|---|
| `CFG_HOME_PATH` | `ARROW_HOME` có dấu cách hoặc ký tự không phải ASCII | Đặt biến `ARROW_HOME`, ví dụ `D:\arrow` |
| `CFG_PARSE` | File cấu hình không phải JSON hợp lệ | Sửa file ở dòng và cột được báo, hoặc chạy `arrow config path` để tìm file |
| `CFG_INVALID` | Sai kiểu, sai khoảng giá trị, tham chiếu không tồn tại | In khoá, giá trị và nguồn |
| `CFG_NEWER_SCHEMA` | Cấu hình của bản ARROW mới hơn | Cập nhật ARROW |
| `CFG_SECRET_IN_PROJECT` | `.arrow.json` có khoá API | Xoá khoá khỏi file, dùng `arrow secrets set` |
| `CFG_NO_MODEL` | Chưa chọn model | Chạy `arrow setup` |
| `CFG_SECRET_MISSING` | Provider cần khoá mà không tìm thấy | `arrow secrets set <ref>` hoặc đặt `ARROW_SECRET_<REF>` |
| `CFG_TOOL_MISSING` | Thiếu công cụ mà không tải được | `arrow tools install` khi có mạng |
| `CFG_TOOL_CHECKSUM` | File tải về sai sha256 | Thử lại; nếu vẫn sai thì kiểm tra proxy hoặc mirror |
| `CFG_DISK_FULL` | Không đủ chỗ trống trong `ARROW_HOME` (dưới 2 GB) | `arrow clean --work --runs` |

## 10. Interface

```csharp
public interface IConfigStore
{
    EffectiveConfig Load(string? projectRoot, IReadOnlyDictionary<string, string?> cliOverrides);
    void Set(ConfigScope scope, string key, string rawValue, string? projectRoot);
    void Unset(ConfigScope scope, string key, string? projectRoot);
}

public interface ISecretStore
{
    SecretValue? Get(string reference);
    void Set(string reference, SecretValue value);
    bool Remove(string reference);
    IReadOnlyList<(string Reference, string Last4)> List();
}

public interface IRunStore
{
    RunHandle Create(RunInfo info);
    RunHandle Open(string runIdOrPrefix);
    IReadOnlyList<HistoryEntry> History(int limit);
    void WriteJson<T>(RunHandle run, string relativePath, T value);
    T? ReadJson<T>(RunHandle run, string relativePath);
}

public interface IToolStore
{
    IReadOnlyList<ToolStatus> Check();
    Task<string> EnsureAsync(string toolId, IProgress<ArrowEvent> progress, CancellationToken ct);
}
```

## 11. Kiểm thử

**Unit:**
- Gộp 5 tầng: từng khoá ghi đè, nguồn đúng.
- Biến môi trường dạng `__`; parse giá trị theo kiểu.
- `CFG_SECRET_IN_PROJECT`.
- `schema_version` cũ hơn và mới hơn.
- `config set` giữ nguyên các khoá khác.
- `SecretStore`: ghi rồi đọc lại; thứ tự ưu tiên biến môi trường; quyền 600 trên Linux.
- `Redactor` che khoá trong chuỗi có header `Authorization`.
- `run_id` duy nhất khi tạo 1.000 lần liên tiếp; tìm theo tiền tố.
- `tools install`: sha đúng, sha sai, mất mạng (HTTP giả).

**Integration:**
- Chạy `gen` với `FakeLlmClient` trên fixture, rồi kiểm tra không file nào dưới `runs/` hay `logs/` chứa khoá thử nghiệm `sk-test-SECRET-123`.

**Nghiệm thu:**
- `arrow config list --origin` in đúng nguồn của từng khoá.
- Xoá `ARROW_HOME`, chạy `arrow setup`, rồi chạy `arrow gen` với `--json`: chạy được và có đủ cấu trúc `runs/` như Mục 5.2.
