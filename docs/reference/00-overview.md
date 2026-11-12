# 00. Tổng quan ARROW Framework

**Trạng thái:** spec chờ duyệt (13/10/2026). Chưa có code. Code C# console viết sau khi spec được duyệt.

ARROW Framework là công cụ dòng lệnh (C# console, .NET 8) chạy toàn bộ quy trình ARROW trên một tập CUT (class under test) lấy từ manifest: phân tích repository, sinh unit test bằng LLM, xác minh và sửa test, đánh giá chất lượng bộ test. Framework gồm **4 khối**, mỗi khối có một file spec riêng:

| Khối | Spec | Việc chính | Đầu ra chính |
| --- | --- | --- | --- |
| 1. Repository-Aware | `01-repository-aware.md` | Tải đúng commit, phân tích 6 thông tin cấu hình, chạy baseline | Repository Metadata **M**, baseline |
| 2. LLM-Based Test Generation | `02-llm-test-generation.md` | Dựng prompt, chọn quy trình prompting, gọi model qua lớp trừu tượng, tách mã test | Candidate Unit Test **T** |
| 3. Adaptive Repair Strategy | `03-adaptive-repair.md` | Biên dịch và chạy test (5 bước xác minh), chẩn đoán lỗi, quyết định sửa tiếp, chốt bộ test | Final Test Suite **S** và log từng vòng |
| 4. Automated Test Suite Assessment | `04-test-suite-assessment.md` | Đo coverage (JaCoCo), mutation (PIT), test smell (tsDetect), tổng hợp báo cáo | Unified Metrics, báo cáo |

## 1. Luồng dữ liệu

```
manifest (CUT) ──► [Khối 1] ──M, baseline──► [Khối 2] ──T──► [Khối 3] ──S (VALID)──► [Khối 4] ──► báo cáo
                                                ▲              │
                                                └─RepairRequest┘   (vòng sửa, nét đứt trong hình)
```

1. Khối 1 nhận một bản ghi manifest, tạo workspace đúng commit, trả về **M** (metadata repository) và **baseline** (test sẵn có pass ổn định).
2. Khối 2 nhận M và CUT, sinh **T** cho mỗi tổ hợp (model × quy trình prompting).
3. Khối 3 xác minh T qua 5 bước. T đạt VALID thì chuyển sang khối 4. T không đạt thì chẩn đoán lỗi; nếu còn được sửa, thuật toán sửa tạo **RepairRequest** gửi ngược lại khối 2 (cùng lớp gọi model) để nhận T mới, rồi xác minh lại. Hết lượt sửa thì chốt bộ test.
4. Khối 4 đo chất lượng các bộ test VALID và tổng hợp mọi số liệu (xác minh, sửa, chất lượng, chi phí) thành báo cáo.

Ảnh framework chỉ là cái nhìn tổng quan. Spec này bổ sung những phần thực nghiệm cần mà ảnh không vẽ: **Fixed Repair** (đối chứng của RQ2) chạy cùng vòng lặp của khối 3, và **EvoSuite** (hệ thống so sánh của RQ3) sinh bộ test được xác minh bằng khối 3 rồi đo bằng khối 4.

## 2. Khái niệm và định danh

| Khái niệm | Định nghĩa | Định danh |
| --- | --- | --- |
| Task | Một CUT trong manifest | `task_id` (lấy từ manifest), kèm `dataset` |
| Candidate | Một test do khối 2 sinh cho một task | `(task_id, model, strategy)` |
| Chain | Quá trình sửa một candidate bằng một cơ chế | `(task_id, model, strategy, mechanism)`; `mechanism` ∈ `fixed`, `adaptive` |
| Round | Một vòng sửa trong chain | `round` = 1…N; `round 0` là candidate ban đầu (c0) |
| Suite | Bộ test cuối cùng đem đo ở khối 4 | `suite_id` = `<model>__<strategy>__<mechanism>` hoặc `<model>__<strategy>__initial` (VALID ngay lần sinh) hoặc `evosuite` |
| Run | Một lần chạy framework trên một tập task | `run_id` (người dùng đặt hoặc tự sinh `run-<yyyyMMdd-HHmm>`) |

### 2.1. Thuật ngữ

| Thuật ngữ | Nghĩa | Ví dụ |
| --- | --- | --- |
| CUT | *Class under test*: class cần sinh test | `OrderService` |
| FQN | *Fully qualified name*: tên đầy đủ của class gồm package | `com.acme.shop.OrderService` |
| Manifest | File `.jsonl` liệt kê các CUT của dataset, mỗi dòng một CUT (repository, commit, đường dẫn) | `evaluation_manifest/pilot/classes2test_pilot_50_seed42_v2.jsonl` |
| Revision status | Mức chắc chắn của commit, trường `revision_verification_status`: `UPSTREAM_PINNED` (đúng commit dataset ghi) hoặc `CONTENT_MATCHED` (preflight tìm commit có nội dung khớp). Trường `revision_provenance` cho biết commit lấy từ đâu: `dataset_record` hoặc `git_history` | Classes2Test: `git_history` + `CONTENT_MATCHED` |
| Build root | Thư mục chạy Maven/Gradle (có file build); có thể là thư mục con của repository. `module_path` của manifest tính từ đây | `3rdParty/libsodium-jni-2.0.1` |
| Snapshot | Bản sao workspace đã biên dịch, chưa chạy test; mỗi lần chạy test dùng một bản sao mới của nó | |
| Mirror | Bản sao git của repository trên máy, dùng chung cho mọi run | `repo/mirrors/github.com/acme/shop.git` |
| Workspace | Thư mục mã nguồn ở đúng commit để build; mỗi lần chạy test dùng một bản sao mới của snapshot | `repo/ws/<run>/<task>/…` |
| Baseline | Kết quả chạy test sẵn có của module **trước khi** thêm test sinh ra | 42 test pass ở cả 2 lần chạy |
| M | Repository Metadata: 6 thông tin cấu hình của khối 1 | build tool Maven, Java 8, JUnit 4… |
| T, c0 | Candidate Unit Test; c0 là bản do khối 2 sinh lần đầu (chưa sửa) | `OrderService_ArrowTest.java` |
| Chain | Quá trình sửa một candidate bằng một cơ chế (Fixed hoặc Adaptive), tối đa N vòng | |
| Checkpoint | Bản tốt nhất tới hiện tại trong chain Adaptive; vòng sau sửa từ bản này | |
| Error signature | Lỗi đã chuẩn hóa (bỏ số dòng, tên, giá trị) để nhận ra lỗi lặp | `COMPILE \| cannot find symbol \| method` |
| Code hash | SHA-256 của mã test sau khi bỏ comment và khoảng trắng; để nhận ra mã trùng | `sha256:9f3c…` |
| INFRA | Lỗi hạ tầng: không phải lỗi của test (timeout build, mạng, công cụ) | Không tải được dependency |
| EXCLUDED | Task không dùng được, bị loại kèm lý do | Repository không còn commit |

Ví dụ xuyên suốt cả 4 khối dùng một CUT minh họa (số liệu giả định): repository `https://github.com/acme/shop`, module `shop-core`, CUT `com.acme.shop.OrderService`, Maven, Java 8, JUnit 4 + Mockito. Mỗi spec 01–04 có mục "Ví dụ" theo CUT này.

**Trạng thái xác minh** (định nghĩa đầy đủ ở spec 03): `COMPILE < UNKNOWN < DISCOVERY < RUNTIME < ASSERTION < REGRESSION < VALID`. `INFRA` (timeout, lỗi công cụ, lỗi mạng) và `EXCLUDED` (task không dùng được) nằm ngoài chuỗi.

## 3. Cấu hình

Ba file, tách phần dùng chung với phần riêng của từng máy.

### 3.1. `experiments/<tên>.json` (dùng chung, commit)

```json
{
  "experiment_id": "pilot-v1",
  "manifests": [
    { "dataset": "classes2test", "path": "../evaluation_manifest/pilot/classes2test_pilot_50_seed42_v2.jsonl", "sha256": "…", "repair": true },
    { "dataset": "gitbug-java",  "path": "../evaluation_manifest/pilot/gitbug_java_pilot_9_seed42.jsonl", "sha256": "…", "repair": false }
  ],
  "models": [
    { "name": "gpt-6-luna", "expected_model_id_prefix": "gpt-6-luna",
      "params": { "reasoning_effort": "none", "temperature": 0, "max_tokens": 16384, "seed": 42 },
      "price_usd_per_million": { "input": 0.05, "output": 0.25 } }
  ],
  "strategies": ["zero_shot", "zero_shot_project_aware", "few_shot"],
  "repair": { "mechanisms": ["fixed", "adaptive"], "n": 10, "early_stop_p": null },
  "verification": { "baseline_runs": 2, "step_timeout_seconds": 900, "infra_attempts": 3 },
  "screening": { "require_paired_test_pass": true },
  "assessment": { "tools": ["jacoco", "pit", "tsdetect"], "comparison_systems": ["evosuite"] },
  "prompts": { "dir": "prompts", "version": "v1" },
  "generated_test_suffix": "_ArrowTest"
}
```

- `sha256` của manifest được kiểm tra khi đọc; lệch thì dừng (`MANIFEST_MISMATCH`).
- `repair: false` ở một manifest: candidate của dataset đó chỉ xác minh, không sửa (ví dụ GitBug-Java ở pilot).
- `repair_subset: K` ở một manifest (thực nghiệm chính, Classes2Test `K = 120`), cùng `repair.rq3_strategy` và `repair_subset_file` ở cấp cấu hình: RQ2 chỉ sửa K ứng viên thất bại rút đều theo ô; thực nghiệm chạy hai pha (spec 03 Mục 6.1).
- File này quyết định kết quả thực nghiệm. Run ghi sha256 của file vào `run.lock.json`; chạy tiếp một run với file đã đổi thì từ chối (`RUN_CONFIG_MISMATCH`).

### 3.2. `machine.local.json` (riêng từng máy, không commit)

```json
{
  "machine_name": "Dung-mac",
  "jdk_homes": { "8": "…", "11": "…", "17": "…", "21": "…" },
  "maven_path": "/opt/homebrew/bin/mvn",
  "gradle_path": "/opt/homebrew/bin/gradle",
  "git_path": "/opt/homebrew/bin/git",
  "work_dir": "../repo",
  "tools_dir": "../tools",
  "llm": { "proxy_url": "http://127.0.0.1:4000", "proxy_mode": "auto_start", "proxy_config_file": "litellm.config.local.yaml", "rpm_limit": 60 },
  "workers": { "build": 4, "llm": 2 }
}
```

### 3.3. `litellm.config.local.yaml` (riêng từng máy, không commit)

Mỗi model trong `experiments/*.json` có một mục trong `model_list` (provider, endpoint, khóa API). Chi tiết ở spec 02 Mục 4.4. Khóa API chỉ nằm ở file này, không bao giờ ghi vào log hay kết quả.

## 4. Thư mục của một run

```
runs/<run_id>/
  run.lock.json            cấu hình đã khóa: sha256 experiment, manifest, prompt; commit của tool; JDK, Maven, Gradle; máy
  ledger.jsonl             mỗi bước xong một dòng (để chạy tiếp sau khi dừng)
  events.jsonl             sự kiện để theo dõi (bắt đầu/kết thúc bước, lỗi)
  llm_cache/               lượt gọi model đã hoàn thành, khóa = định danh lượt gọi + request; chỉ để phát lại khi chạy tiếp (spec 02 Mục 4.4)
  tasks/<dataset>/<task_id>/
    block1/                metadata.json (M), baseline.json, workspace.json, logs/
    block2/<model>/<strategy>/   prompt.json, response.json, usage.json, candidate.java, candidate.json
    block3/<model>/<strategy>/
      verify_c0.json       kết quả xác minh candidate ban đầu
      <mechanism>/round_<k>/     request.json, prompt.json, response.json, candidate.java, verify.json, decision.json
      <mechanism>/final.json     Final Test Suite của chain
    block3/evosuite/       bộ test EvoSuite và kết quả xác minh
    block4/<suite_id>/     jacoco.json, pit.json, tsdetect.json, logs/
  reports/                 CSV và report.md của khối 4
```

Mọi file JSON: UTF-8 không BOM, xuống dòng LF, tên trường snake_case. Ghi file theo kiểu nguyên tử (ghi `.tmp` rồi đổi tên).

## 5. Thực thi

### 5.1. Lệnh

| Lệnh | Việc |
| --- | --- |
| `arrow run [--experiment F] [--run-id R] [--tasks FILE\|ID,…] [--shard F] [--blocks 1,2,3,4]` | Chạy các khối cho các task; mặc định cả 4 khối, mọi task của experiment; `--shard` chỉ chạy các task được giao cho máy này (Mục 5.4) |
| `arrow analyze …` | Chỉ khối 1 |
| `arrow rq2-subset --experiment F --run-id R [--run-id R2 …]` | Sau pha 1: chọn tập con cố định của RQ2 (`repair_subset`) từ kết quả c0 của mọi run, ghi `repair_subset_file` để commit (spec 03 Mục 6.1) |
| `arrow screen --manifest F --run-id R` | Chỉ khối 1 cho mọi CUT của manifest, ghi `reports/screening.csv` (tiêu chí B) làm đầu vào cho script chọn mẫu (spec 01 Mục 6.3) |
| `arrow generate …` | Khối 2 (cần khối 1 đã xong) |
| `arrow repair …` | Khối 3 (cần khối 2 đã xong) |
| `arrow assess …` | Khối 4 phần đo (cần khối 3 đã xong) |
| `arrow report --run-id R [--run-id R2 …]` | Khối 4 phần báo cáo; nhiều run thì gộp (dùng khi nhiều máy chạy các phần task khác nhau) |
| `arrow doctor` | Kiểm tra môi trường: JDK, Maven/Gradle, Git, LiteLLM, công cụ khối 4, gọi thử từng model |
| `arrow status --run-id R` | Tiến độ: số job xong/tổng theo khối, số INFRA, số EXCLUDED |
| `arrow show --run-id R --task T` | Chi tiết một task: kết quả từng khối, từng vòng sửa |
| `arrow rerun-list --run-id R` | Danh sách job cần chạy lại riêng, kèm lệnh |
| `arrow control --run-id R pause\|resume\|stop` | Điều khiển run đang chạy từ terminal khác |
| `arrow setup` / `arrow models` | Cài đặt máy; model, provider, khóa API, giá |
| `arrow` (không kèm lệnh) | Menu chính |

Màn hình của từng lệnh (menu, cài đặt, theo dõi khi chạy, chi tiết task, báo cáo…) mô tả ở **spec 06**: nhập bằng `ReadLine`, xuất bằng `WriteLine`. Khi chạy, gõ `p` tạm dừng, `r` tiếp tục, `s` hoặc Ctrl+C dừng an toàn (việc đang chạy dở bị hủy, việc đã ghi ledger được giữ, chạy lại cùng lệnh thì tiếp tục), `t` in bảng tổng.

### 5.2. Thứ tự và song song

- Đơn vị lập lịch là **job**: `(task, khối, model, strategy, mechanism)`. Một job chỉ chạy khi job nó phụ thuộc đã xong (khối 1 → khối 2 → xác minh c0 → chain sửa → khối 4).
- Hai hàng đợi giới hạn song song: `workers.build` (mọi lệnh Maven/Gradle/Java) và `workers.llm` (lượt gọi model). Một chain sửa chiếm lần lượt hai hàng đợi theo từng vòng.
- Mỗi job build trong **workspace riêng** (bản sao của mirror ở đúng commit), không dùng chung thư mục build.
- Fixed và Adaptive của cùng một candidate chạy **xen kẽ** theo thứ tự task (task lẻ: Fixed trước; task chẵn: Adaptive trước), như `Adaptive repair.md` Mục 8.

### 5.3. Chạy tiếp và chạy lại

- Job ghi một dòng `ledger.jsonl` khi xong (kể cả kết quả lỗi). **Dòng ledger là việc ghi cuối cùng của job**, chỉ ghi sau khi mọi file đầu ra của job đã lưu đủ (ví dụ khối 1: metadata, snapshot, baseline, tiêu chí B; spec 01 Mục 6.4). Chạy lại cùng `run_id` bỏ qua job đã có dòng ledger; job chưa có dòng thì làm lại từ đầu job (xóa đầu ra dở dang).
- Lỗi hạ tầng (timeout, dependency, công cụ) được thử lại tự động, tối đa `verification.infra_attempts` = 3 lần thử. Vẫn lỗi thì ghi ledger với `outcome = INFRA`, `needs_rerun = true` và lý do; báo cáo có danh sách **"Cần chạy lại riêng"** kèm lệnh cho từng task: `arrow run --run-id R --tasks <task_id> --rerun-infra`. `--rerun-infra` không kèm `--tasks` thì chạy lại mọi job INFRA của run.
- Một chain sửa dừng giữa chừng (Ctrl+C) được chạy lại **từ đầu chain**; các lượt gọi model đã có trong `llm_cache/` không tốn tiền lại.

### 5.4. Nhiều máy

Task được chia cho các máy bằng file phân công `shards/<plan>/shard-NN.json` (commit lên Git; mỗi file có `experiment_sha256` và danh sách `tasks[].task_id`; mọi CUT của một repository nằm cùng một máy). Mỗi máy chạy `arrow run --experiment experiments/<tên>.json --run-id <plan>-<máy> --shard shards/<plan>/shard-NN.json`; framework từ chối nếu `experiment_sha256` của shard khác sha256 của cấu hình đang dùng (`RUN_CONFIG_MISMATCH`). Cuối cùng `arrow report --run-id A --run-id B …` gộp kết quả; báo cáo kiểm tra mọi run cùng sha256 experiment và prompt, task không trùng nhau. Thứ tự Fixed/Adaptive của một task (Mục 5.2) theo vị trí của task trong toàn bộ thực nghiệm, nên giống nhau dù task chạy ở máy nào. `--tasks <file danh sách task_id>` vẫn dùng được cho chạy lại riêng.

## 6. Log và mã lỗi

- Log của tool: `logs/arrow-<yyyyMMdd>.log` (mọi lệnh, lỗi, cảnh báo). Log từng lệnh build: `tasks/…/logs/<bước>.log`, dòng đầu ghi lệnh, thư mục, JDK; dòng cuối ghi mã thoát, thời gian, có timeout không. Log lớn hơn 5 MB chỉ giữ 2 MB đầu và 2 MB cuối.
- Không ghi khóa API, `master_key`, hay `api_base` đầy đủ vào bất kỳ log hay file kết quả nào; chỉ ghi tên máy chủ của endpoint.
- Mã lỗi viết hoa, có tiền tố theo khối: `B1_CLONE_FAILED`, `B2_INVALID_OUTPUT`, `B3_TIMEOUT`, `B4_PIT_FAILED`… Danh sách đầy đủ trong từng spec.

## 7. Kiến trúc code C#

Chi tiết ở **spec 07**. Kiến trúc **3 tầng**, các tầng gọi nhau qua interface:

| Tầng | Project | Làm |
| --- | --- | --- |
| UI | `Arrow.UI` | Nhập bằng `Console.ReadLine`, xuất bằng `Console.WriteLine`; menu và màn hình (spec 06) |
| Service | `Arrow.Service` | Thực thi logic: 4 khối, thuật toán sửa, công thức báo cáo, lập lịch; gọi công cụ ngoài (git, Maven/Gradle, Java, model, JaCoCo/PIT/tsDetect/EvoSuite) |
| Data Access | `Arrow.DataAccess` | Đọc, ghi dữ liệu của framework ra file (cấu hình, manifest, prompt, ledger, kết quả, cache, báo cáo, log) |

`Arrow.Models` chứa class dữ liệu dùng chung. Hướng phụ thuộc: UI → Service → Data Access. Mỗi khối có interface khớp đúng input/output trong spec của khối (`IRepositoryAnalyzer`, `ITestGenerator`, `IVerifier`, `IRepairRunner`, `IQualityTool`…). Gọi model chỉ qua `ILlmClient`; test dùng `FakeLlmClient`, không tốn tiền.

## 8. Kiểm thử của framework

| Mức | Nội dung |
| --- | --- |
| Unit | Phân tích file build (Java version, test framework), dựng prompt, tách mã từ response, phân loại lỗi và signature, so sánh checkpoint, thuật toán Fixed/Adaptive theo ví dụ của spec 03, quy tắc N, công thức chỉ số khối 4 |
| Integration | Khối 1 và khối 3 trên 3 fixture với Maven/Gradle thật; khối 4 đo một test VALID trên mỗi fixture |
| End-to-end | `arrow run` trên fixture với `FakeLlmClient`: đủ 4 khối, ra báo cáo |
| Smoke model thật | 2 CUT, giới hạn số lượt gọi, chạy tay khi cần |

## 9. Chờ xác nhận

Giải thích chi tiết từng điểm (bối cảnh, vấn đề, ảnh hưởng tới RQ, các phương án kèm ưu nhược, đề xuất, phải sửa gì nếu chọn khác) ở **spec 05** (`05-open-questions.md`). Spec 00–04 đang viết theo phương án đề xuất.

| # | Điểm | Quyết định (14/10/2026) |
| --- | --- | --- |
| Q1 | Ảnh ghi "Fine-Tuned Open-Source Code Model" | **Chốt:** nhãn "Selected LLM (commercial API / open-weight model)", không fine-tune |
| Q2 | Ảnh không vẽ Fixed Repair và EvoSuite | **Chốt:** Fixed là một cơ chế của khối 3; EvoSuite sinh ở khối 4, xác minh bằng khối 3 |
| Q3 | Bộ test nào đại diện ARROW ở RQ3 | **Chốt:** một bộ mỗi task mỗi model (`zero_shot_project_aware` VALID ngay, nếu không thì sau Adaptive); báo riêng từng model, từng dataset |
| Q4 | Prompt sửa có kèm metadata M không | **Chốt:** M đầy đủ cho mọi prompt sửa, cả Fixed và Adaptive |
| Q5 | Adaptive kẹt ở mức 3 khi mã trùng | **Chốt:** mức 3 không tiến triển thì về mức 2; prompt mức 3 có danh sách đã thử |
| Q6 | Quy tắc chọn N | **Chốt:** quy tắc của CLAUDE.md (5 ≤ k ≤ 10), chạy pilot `n = 10` sau khi code xong |
| Q7 | Prompt mức 2 cho UNKNOWN | **Chốt:** khối lỗi build đầy đủ và cấu hình build |
| Q8 | Timeout | **Chốt:** thử tối đa 3 lần; vẫn timeout → INFRA, ghi rõ cần chạy lại riêng task kèm lệnh |

## 10. Lịch sử thay đổi spec

| Ngày | Thay đổi | Lý do |
| --- | --- | --- |
| 13/10/2026 | Bản đầu spec 00–05 | |
| 14/10/2026 | Chốt Q1–Q8 (spec 05); thêm spec 06 (màn hình), 07 (kiến trúc) | Quyết định của nhóm |
| 14/10/2026 | Spec 01: đọc đúng `revision_verification_status` (CONTENT_MATCHED/UPSTREAM_PINNED) và `revision_provenance` (git_history/dataset_record); tách major từ `java_version` dạng `openjdk version "…"` | Review: đọc nhầm trường manifest |
| 14/10/2026 | Spec 01: thêm build root và module root; `module_path` tính từ build root (lấy từ `build_attempts` của preflight) | Review: mẫu `118477931` có build root `3rdParty/libsodium-jni-2.0.1`, gốc repository không có pom; 23/59 mẫu pilot có `module_path = "."` |
| 14/10/2026 | Spec 01, 03, 04: mọi lệnh Maven chạy từ build root với `-pl <m> -am`, kể cả chạy test | Review: chạy test thiếu `-am` thì module anh em chưa có artifact |
| 14/10/2026 | Spec 02: cache khóa theo `call_id` (lượt gọi cụ thể), chỉ để phát lại khi chạy tiếp, giữ thời gian và chi phí gốc | Review: cache chung theo nội dung làm cơ chế chạy sau được thời gian 0 và làm Fixed lặp output lỗi |
| 14/10/2026 | Spec 01, 03, 04: snapshot sau biên dịch; mỗi lần chạy test (baseline, mỗi vòng xác minh, mỗi lần đo) trên bản sao mới | Review: vòng trước có thể làm hỏng workspace của vòng sau |
| 14/10/2026 | Spec 04: phạm vi đo dùng mẫu chính xác `<FQN>` và `<FQN>$*` cho PIT và JaCoCo | Review: `<FQN>*` khớp nhầm class khác |
| 14/10/2026 | Spec 04: RQ3 ghép cặp theo từng chỉ số, báo `n_pairs` riêng | Review: công cụ lỗi độc lập làm hai bên so trên hai tập task khác nhau |
| 14/10/2026 | Spec 01: tiêu chí B (test người viết chạy và pass ổn định), lệnh `screen`, `require_paired_test_pass` | Review: thống nhất với cách chọn pilot v2 |
| 14/10/2026 | Spec 01, 02, 03, 04: xử lý TestNG (2/50 CUT pilot) | Kiểm tra thêm khi review |
| 14/10/2026 | Spec 01: build root và bộ chọn module lấy từ **lần biên dịch test thành công** trong `build_attempts` (chế độ reactor hoặc standalone), không lấy lần thử đầu | Review lần 2: tập đánh giá có 2 CUT (`103728471_…`, `9580168_…`) mà lệnh reactor thất bại, chỉ build riêng module thành công |
| 14/10/2026 | Spec 01, 04: `dependency:list` / `build-classpath` chạy **cùng một lần gọi Maven** với `test-compile` | Review lần 2 (kiểm chứng bằng fixture): gọi riêng thì thiếu artifact module anh em |
| 14/10/2026 | Spec 03, 04: lệnh chạy test lấy tên từ `SuiteFiles.test_fqn` (`…_ArrowTest`, `…_ESTest`); mô tả `SuiteFiles` gồm scaffolding, runtime EvoSuite | Review lần 2: cố định `_ArrowTest` làm bộ EvoSuite bị xếp DISCOVERY |
| 14/10/2026 | Spec 04: mỗi bản sao để đo phải ghi bộ test và **biên dịch test** trước khi chạy PIT | Review lần 2: snapshot tạo trước khi có bộ test, PIT không tự biên dịch |
| 14/10/2026 | Spec 00, 01, 07: ledger là việc ghi cuối cùng của job; khối 1 chỉ ghi sau baseline và tiêu chí B | Review lần 2: ghi OK sớm làm chạy tiếp thiếu baseline |
| 14/10/2026 | Spec 06, 07: kiến trúc 3 tầng UI / Service / Data Access qua interface; UI chỉ dùng `Console.ReadLine` và `Console.WriteLine` (bỏ Spectre.Console) | Yêu cầu của nhóm |
| 14/10/2026 | Spec 02: đồng bộ với thư mục `prompts/` thật (3 system prompt sinh, 2 user message, prompt sửa theo mức, ví dụ few-shot trong system prompt, đầu ra mã thuần không fence, nguồn gốc few-shot và bước xác minh còn thiếu) | Review prompt |
| 14/10/2026 | Spec 03: mỗi lần "đã thử" có thêm lỗi đầu tiên chưa chuẩn hóa và các dòng mã đã đổi (3 lần gần nhất); mức 3 giữ thông tin bổ sung của mức 2 | Review prompt; cần cập nhật `Adaptive repair.md` Mục 3.1 và 4 |
| 14/10/2026 | Spec 02: giữ `max_tokens` = 8192 (pilot cũ lớn nhất 2.944 token, không lượt nào bị cắt); Qwen qua Ollama cần `num_ctx` ≥ 32.768; doctor kiểm tra `num_ctx` | Quyết định của nhóm |
| 14/10/2026 | Spec 01 (khi code C3): chương trình build theo lệnh preflight thành công (wrapper hay công cụ máy); effective POM / `arrowInfo` là lần gọi riêng trước biên dịch; đọc tên class trong báo cáo Surefire theo `<testsuite>` khi `classname` không phải tên class; bỏ báo cáo trùng của TestNG (`junitreports/`); `screening.csv` thêm cột `message`; tùy chọn của `screen`; mã `B1_WORKSPACE`; dùng đường dẫn thật của `work_dir`; tải commit từ mirror cũ trước khi tải qua mạng | Sàng lọc pilot v2 bằng framework: Surefire 3.5 + JUnit 5 ghi `classname` là tên method (2 CUT ra NO_TEST_RUN sai); wrapper lưu đường dẫn tuyệt đối không chạy được trên bản sao |
| 14/10/2026 | Spec 03 (khi code C4): bước 2 dùng lại bản sao đã biên dịch ở bước 1; lệnh chạy riêng hỏng không có báo cáo → RUNTIME; "file sinh ra" gồm scaffolding; test sinh ra không được chạy ở bước 5 không phải regression; classpath phụ của Maven dùng scope `system`; `verify.json` thêm `build_error_excerpt`, `test_file`; `workspace.json` thêm chương trình build, module, init script | Code Verifier và chạy fixture |
| 14/10/2026 | Spec 02 (khi code C5): rút gọn CUT &gt; 48.000 ký tự bằng lệnh java-helper `shrink_source` (bỏ comment và dòng trống → giữ mọi chữ ký, thân method giữ theo thứ tự tới khi đủ, còn lại `{ /* body omitted */ }`); khối 2 tự ghi `prompt.json` trước lượt gọi, `response.json`, `usage.json`, `candidate.*` sau; gọi lại khi lỗi tạm thời = tối đa 3 lần **gọi lại** (4 lần gọi) | Code C5 |
| 14/10/2026 | Spec 03 (khi code C6): `first_error` theo đúng dạng Mục 6.4 (COMPILE kèm symbol/location, RUNTIME `method: Exception at Class.method`, ASSERTION `method: thông điệp`, tối đa 200 ký tự); `level_up` đếm mỗi lần áp `NextLevel` (kể cả 3 → 2); mã trùng so với mọi hash đã thấy, kể cả bản bị rollback | Code C6 |
| 14/10/2026 | Spec 04 (khi code C7): công cụ khối 4 khóa trong `tools/tools.lock.json` (Maven Central hoặc GitHub Releases; sha256 `null` = chưa khóa, `doctor` báo); PIT cần thêm `pitest-html-report`, `commons-text`, `commons-lang3`, `jopt-simple`, chạy bằng JDK ≥ 11 (JDK của task nếu ≥ 11), classpath qua `--classPathFile`, `--failWhenNoMutations=false`; tsDetect chỉ đọc mã nên chạy trên thư mục tạm chứa file test và CUT; báo cáo thêm `rq1_signatures`, `rq2_mechanisms`, `rq2_by_c0_state`, `rq2_p_sensitivity`, `rq3_availability`, `rq3_arrow_variants`, `rq3_smells`, `cost`, `timing`, `excluded`; `rq2_cumulative` thêm cột `dataset,model`, `rq3_summary` thêm `arrow_sd,evosuite_sd`; gộp nhiều run ghi vào `runs/merged-<run1>-<run2>…/reports/` | Code C7 |
| 14/10/2026 | Spec 00, 07 (khi code C8–C9): đường dẫn manifest trong `experiments/*.json` tính từ thư mục của file đó (vì vậy là `../../evaluation_manifest/…`); run chép cấu hình thực nghiệm vào `runs/<run>/experiment.json`; hàng đợi build giới hạn ở tầng tiến trình (mọi lệnh Maven/Gradle/Java/git); job: `analyze`, `generate/<m>/<s>`, `verify/<m>/<s>`, `chain/<m>/<s>/<mech>`, `evosuite`, `measure/<suite>`; thêm lệnh `tools install`, cờ `--fake-llm` | Code C8, C9 |
| 14/10/2026 | Đổi tên lệnh `arrow-fw` → `arrow`; thêm `arrow run --shard` (file phân công kèm `experiment_sha256`); thứ tự Fixed/Adaptive theo vị trí task trong toàn bộ thực nghiệm (không theo danh sách đã lọc); java-helper khóa khi build jar | Quyết định của nhóm (pilot chạy trên mọi máy theo shard); review nhánh C5–C9 |
| 14/10/2026 | Spec 01 Mục 4.3 bước 5: JVM chạy test không khởi động được với JDK đã chọn → đổi JDK | Quyết định của nhóm; jsoup trên Mac ARM với JDK 8 |
| 15/10/2026 | Spec 01: Gradle dùng project chứa CUT khi lệnh preflight không ghi project hoặc ghi project cha; jar hỏng trong cache Maven/Gradle bị xóa và thử lại (lỗi hạ tầng) | Sàng lọc tập đánh giá 200 CUT: 5 CUT `B1_MODULE_MISMATCH` sai, 5 CUT lỗi biên dịch do jar hỏng trong `~/.m2` |
| 15/10/2026 | Spec 00, 03, 04: tập con cố định 120 ứng viên Classes2Test cho RQ2 rút đều theo ô model × quy trình (seed 42, xếp theo sha256), chạy hai pha với `arrow rq2-subset`; Adaptive cho mọi ứng viên `zero_shot_project_aware` thất bại ngoài tập con, chỉ dùng cho RQ3 (`rq3_only`), không tính vào RQ2 và quy tắc N | Quyết định của nhóm (phương án E): giữ đúng thiết kế 120 của Report 2 mà RQ3 vẫn có đủ bộ đại diện |
| 15/10/2026 | Spec 04: đo JaCoCo trên bộ EvoSuite bằng bản sao `separateClassLoader = false`; bảng tổng của `run` đếm cả lượt gọi model của phần sửa; tìm LiteLLM trong `ARROW/litellm` (venv); thêm `experiments/smoke-real.json` (N = 2) cho chạy thử với model thật | Chạy thử C10 với GPT-6 Luna trên 2 CUT pilot |
| 15/10/2026 | Spec 02: `max_tokens` 8192 → 16384; Qwen qua Ollama cần `num_ctx` ≥ 40.960 (doctor kiểm tra) | Pilot shard-01: 2 lượt sinh của `67934497` bị cắt ở 8.192 token (`finish_reason = "length"`, test tự viết stub `ResultSet` đầy đủ); quyết định của nhóm, chạy lại pilot |
