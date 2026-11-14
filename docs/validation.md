# Chạy thử trên repo thật (GĐ7)

Bản nháp, cập nhật 14/11/2026. Nhóm cần xem và đồng ý bảng này (PLAN GĐ7, "xong khi").

## 1. Bộ repo

- Danh sách: [`validation-set.json`](validation-set.json), rút bằng [`tools/validation/sample_validation_set.py`](../tools/validation/sample_validation_set.py), seed `20261114`.
- Nguồn: `technical_eligible_manifest.jsonl` của 5 shard. `strict_eligible_manifest.jsonl` rỗng ở mọi shard (chưa có revision map được audit), nên Chính chọn dùng bản technical (14/11/2026). Sha256 của từng file nguồn có trong `validation-set.json`.
- Kết quả rút: 20 repo, 23 class, từ 3.336 repo có sẵn. Phần lớn repo chỉ có 1 class trong manifest, nên chưa đạt "2–3 class mỗi repo" của PLAN.
- Đã chạy 10 repo (13 class), đúng mức tối thiểu của PLAN, theo quyết định của Chính. Ngoài 2 repo của đợt thử, 8 repo được chọn để phủ các trường hợp đợt thử chưa có: Gradle, TestNG, JUnit 5, Java 17, module lồng nhiều cấp.

## 2. Cách chạy

```
arrow gen <FQN…> --repo <url> --ref <checkout_sha> --plain --yes --max-tokens 200000
```

- Model: `gpt-6-luna` qua TeamoRouter, `temperature=0`; repair adaptive N = 5.
- Đủ 4 khối (có đo JaCoCo, PIT, tsDetect). Project từ GitHub nên test chỉ lưu trong `runs/`.
- Máy: Windows 11, Maven 3.9.16, Gradle 8.14.3, JDK 8/11/17/21.
- Ollama: chưa có trên máy, chưa chạy.

## 3. Kết quả

| # | Repo | Build | Class | Kết quả | Vòng | Line | Branch | Mutation | Smell | Token | Thời gian |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| 7 | Teradata/jaqy | Maven, JUnit 4 | `ArgumentParser` | VALID ở c0 | 0 | 97% | 96% | 87% | 2.00 | 3,6k | 182 s |
| 7 | Teradata/jaqy | | `PathUtils` | VALID ở c0 | 0 | 77% | 52% | 44% | 2.75 | 4,0k | 102 s |
| 9 | Cosium/spring-data-jpa-entity-graph | Maven, JUnit 4 | `DynamicEntityGraph` | VALID ở c0 | 0 | 78% | 75% | 83% | 2.43 | 2,9k | 228 s |
| 9 | Cosium/spring-data-jpa-entity-graph | | `NamedEntityGraph` | RUNTIME → VALID | 1 | 95% | 75% | 77% | 4.43 | 5,7k | 134 s |
| 10 | h908714124/jbock | Gradle, JUnit 5 | `Optionalish` | VALID ở c0 | 0 | 22% | 17% | 0% | 1.00 | 3,1k | 418 s |
| 0 | Johnnei/JavaTorrent | Maven, JUnit 5 | `TorrentManager` | RUNTIME → VALID | 1 | 70% | 17% | (PIT lỗi) | 6.00 | 9,4k | 279 s |
| 2 | marschall/threeten-jpa | Maven, JUnit 5 | `LocalTimeConverter` | VALID ở c0 | 0 | 100% | 100% | (PIT lỗi) | 2.00 | 2,3k | 206 s |
| 15 | aicis/fresco | Maven, JUnit 4, module `tools/mascot` | `Mascot` | VALID ở c0 | 0 | 6% | 0% | 0% | 1.00 | 3,8k | 830 s |
| 15 | aicis/fresco | | `MascotResourcePoolImpl` | VALID ở c0 | 0 | 75% | 50% | 85% | 3.00 | 4,2k | 319 s |
| 17 | google/auto | Maven, JUnit 4 | `AnnotationValues` | RUNTIME → VALID | 1 | 58% | 100% | 45% | 2.86 | 14,6k | 293 s |
| 18 | otros-systems/otroslogviewer | Gradle 3.3, TestNG | `JsonLogParser` | REGRESSION (dừng: stagnation) | 3 | - | - | - | - | 17,4k | 1.874 s |
| 14 | sterodium/selenium-grid-extensions | Maven, module lồng 2 cấp | `ResourceUploadRequest` | BLOCKED `B1_DEPENDENCY_RESOLUTION` | - | - | - | - | - | 0 | 86 s |
| 1 | billyean/acadia | Maven, TestNG | `Amazon` | INFRA `B1_BASELINE_TIMEOUT` | - | - | - | - | - | 0 | 2.782 s |

Tóm tắt:
- **10/13 class VALID** (77%); tính riêng các class đã tới được khối 2 thì 10/11.
- Với 10 class VALID: trung bình 0,3 vòng sửa, 5,4k token và 299 s mỗi class. Thời gian của class đầu mỗi repo có cả khối 1 (85–524 s, gồm baseline ×2); class sau cùng module dùng chung khối 1.
- Mọi lần không VALID đều có mã lỗi và gợi ý cách sửa.
- Tổng token của cả GĐ7: khoảng 308k, gồm 237k của lần chạy `otroslogviewer` gặp lỗi 8 ở mục 5.

Nhận xét:
- **VALID nhưng test yếu:** `Optionalish` (1 test, mutation 0%) và `Mascot` (line 6%) qua được 5 bước xác minh nhưng gần như không kiểm tra gì. Khối 4 phát hiện được điều này; người dùng xem số đo trước khi ghi file.
- **`otroslogviewer`:** 19 test `LoadingRunnableTest` pass ở cả 2 lần baseline nhưng fail khi chạy chung với test mới, ở c0 và ở cả 3 vòng sửa. Đây là test về thời gian (pause/resume), có thể là flaky khi máy tải nặng, cũng có thể test mới thật sự ảnh hưởng tới trạng thái chung. Chưa kết luận.
- **`selenium-grid-extensions`:** Maven không phân giải được dependency của module `extension-clients/file-extension-client` (chưa xem artifact nào thiếu). ARROW dừng ở khối 1, chưa gọi model.
- **`acadia`:** baseline lần 1 quá thời gian 3 lần liên tiếp (khoảng 15 phút mỗi lần), không gọi model. Từ quyết định 2 ở mục 6, ARROW dừng ngay sau lần quá giờ đầu tiên.

## 4. Diễn giải kết quả

Mục này giúp đọc đúng các con số ở mục 3, nhất là khi đưa vào báo cáo.

### 4.1 "VALID" và "test có giá trị" là hai việc khác nhau

VALID nghĩa là bộ test qua đủ 5 bước xác minh trên build thật: biên dịch được, được tìm thấy, chạy không lỗi, các assertion đúng, và không làm hỏng test sẵn có. VALID **không** nói bộ test kiểm tra được bao nhiêu. Điều đó do khối 4 đo.

Chia 13 class theo hai tiêu chí:

| Nhóm | Số class | Class |
|---|---:|---|
| VALID, line ≥ 50% và mutation ≥ 50% | 4 | `ArgumentParser`, `DynamicEntityGraph`, `NamedEntityGraph`, `MascotResourcePoolImpl` |
| VALID, chưa đo được mutation (PIT lỗi), line ≥ 50% | 2 | `TorrentManager` (line 70%, branch 17%), `LocalTimeConverter` (line 100%) |
| VALID nhưng một chỉ số thấp | 2 | `PathUtils` (mutation 44%), `AnnotationValues` (mutation 45%) |
| VALID nhưng gần như không kiểm tra gì | 2 | `Optionalish` (line 22%, mutation 0%), `Mascot` (line 6%) |
| Không VALID | 3 | `JsonLogParser` (REGRESSION), `ResourceUploadRequest` (BLOCKED), `Amazon` (INFRA) |

Vì vậy:
- **tỷ lệ VALID là 10/13**;
- **tỷ lệ test vừa VALID vừa có giá trị (line và mutation ≥ 50%) chỉ là 4/13**, có thể lên 6/13 nếu hai class chưa đo được mutation cũng đạt.

Ngưỡng 50% chỉ dùng để đọc bảng này, không có trong spec. Hai class ở nhóm "gần như không kiểm tra gì" là đúng những class mà cảnh báo `B4.WeakSuite` (quyết định 3 ở mục 6) sẽ báo cho người dùng.

### 4.2 Mẫu nhỏ và không ngẫu nhiên

- **Cỡ mẫu:** 13 class. Khoảng tin cậy 95% (Wilson) của tỷ lệ VALID 10/13 là **khoảng 50%–92%**; của tỷ lệ 4/13 là khoảng 13%–58%. Không nên viết "ARROW đạt 77%" như một độ chính xác.
- **Cách chọn:** 2 repo đầu là 2 repo đầu tiên có từ 2 class của danh sách seed; 8 repo sau được chọn tay để phủ các kiểu project (Gradle, TestNG, JUnit 5, Java 17, module lồng). Mẫu này thiên về độ phủ kiểu project, không đại diện cho repo Java nói chung.
- **Nguồn:** manifest `technical_eligible`, tức là các class mà preflight của nhóm đã kiểm là build được. Repo ngoài thực tế có thể khó hơn.

Cách viết phù hợp trong báo cáo: "Chạy thử trên 13 class của 10 repo thật, 10 class cho bộ test VALID; 4 class đạt line và mutation từ 50% trở lên."

### 4.3 Chỉ một cấu hình

- Một model (`gpt-6-luna`, `temperature=0`), một máy Windows 11, chỉ chạy với `--repo`.
- Chưa thử: Linux, Ollama, model khác, và đường ghi file vào project thật trên repo thật (`--project`). Đường ghi file đã có E2E trên fixture (P06), nhưng chưa có trên repo thật.

### 4.4 Thất bại chủ yếu do repo, không do model

- 2/3 class không VALID dừng ở khối 1, trước khi gọi model: repo không phân giải được dependency (`selenium-grid-extensions`), và repo có test treo (`acadia`). Người dùng thật cũng sẽ gặp các repo như vậy; điều quan trọng là ARROW dừng sạch, có mã lỗi và gợi ý.
- Chỉ 1/11 class đã tới khối 2 là không VALID (`JsonLogParser`), và nguyên nhân chưa rõ: 19 test về thời gian fail khi chạy chung với test mới. Đó có thể là test flaky khi máy tải nặng, hoặc test mới thật sự ảnh hưởng tới trạng thái chung. Cần chạy lại module nhiều lần, có và không có test mới, mới kết luận được.

### 4.5 Điều kết quả này chứng minh được

Với tiêu chí "xong khi" của PLAN GĐ7, kết quả đã đủ:
- không lần chạy nào làm hỏng project hay ghi sai chỗ;
- mọi lần thất bại đều có mã lỗi và gợi ý cách sửa;
- 8 lỗi tìm được đều đã sửa, mỗi lỗi có test hồi quy.

Kết quả **chưa** đủ để nói về chất lượng sinh test của ARROW nói chung. Nếu báo cáo cần con số đó, nên chạy nốt 10 repo còn lại của `validation-set.json` theo đúng thứ tự seed, không chọn tay; ước tính 60–100k token và 1–2 giờ máy.

## 5. Lỗi phát hiện và đã sửa

| # | Lỗi | Sửa | Test hồi quy |
|---|---|---|---|
| 1 | Cột "Ghi file" của bảng tổng in tên enum (`remoteproject`, `notvalid`) | Nhãn tiếng Việt (`chỉ trong runs/`, `đã ghi`, …); `--json` giữ nguyên | `GenCommandTextTests` |
| 2 | Chạy nhiều class: cảnh báo cuối không nói thuộc class nào | Mỗi cảnh báo mở đầu bằng tên class | `ThreeClassesOfAMultiModuleProject` |
| 3 | Tên class dính `\r` hoặc khoảng trắng (danh sách dán từ file Windows) làm cả run dừng với `CLI_INTERNAL`, vì tên chưa tìm thấy được dùng làm tên thư mục | Cắt khoảng trắng ở đầu và cuối tên; ký tự không hợp lệ trong tên thư mục `targets/` được thay bằng `_` | `TargetDirTests`, `AMissingClassDoesNotStopTheOthers` |
| 4 | Gợi ý lỗi in nguyên placeholder: `arrow gen --resume {run_id}`, `file log: {log_path}` | `ErrorCatalog.With` điền `run_id` và `log_path` của run | `ContextValuesFillHintsThatTheCallerLeftOpen` |
| 5 | `doctor` không kiểm JaCoCo / PIT / tsDetect (P08 §2.6) | Dòng mới: phiên bản, số file đã tải, sha256 | `MeasuringToolsLineShowsVersionsAndState` |
| 6 | Project Gradle có subproject không có file build riêng (cấu hình từ `build.gradle` gốc): không liệt kê được class nào, `B1_CUT_MISSING` (otroslogviewer) | Các thư mục được `include` trong settings và `<modules>` của Maven cũng là module | `SourceRootsTests` |
| 7 | Chạy tiếp một run bị dừng đột ngột (kill, tắt máy): `git worktree add` lỗi vì `work/<run>` cũ còn sót lại (acadia) | Xoá `work/<run>` cũ trước khi bắt đầu | `AStoppedRunIsResumed` |
| 8 | Test TestNG có tham số mang `toString()` của tham số trong tên (72.000 ký tự): prompt repair nặng 400 KB, 3 lượt gọi tốn 237k token (otroslogviewer) | Tên test trong prompt tối đa 200 ký tự, thông điệp tối đa 300 (ghi vào P04 §4.2). Chạy lại: 4 lượt gọi tốn 17,4k token | `LongParameterizedTestNamesAreCut` |

Không lần chạy nào làm hỏng project hay ghi sai chỗ: project từ GitHub chỉ được ghi trong `runs/`. Đường ghi file vào project local đã được E2E kiểm (P06), nhưng GĐ7 chưa chạy lại trên repo thật dưới dạng bản clone với `--project`.

## 6. Quyết định (Chính, 14/11/2026)

1. **PIT với JUnit 5 cũ:** không thêm bản plugin cũ hơn; ARROW giữ `B4_PIT_FAILED` cho các project này (ghi vào P05 §7).
2. **Baseline quá thời gian:** dừng ngay ở lần đầu, không thử lại; lỗi mạng vẫn được thử lại (P01 §6.2 bước 7; test `ABaselineTimeoutIsNotRetried`).
3. **VALID nhưng test yếu:** cảnh báo khi line < 30% hoặc mutation = 0%, in trước câu hỏi ghi file (P05 §7; test `WeakSuiteTests`, `TheWeakSuiteWarningIsPrinted`).

Bối cảnh của ba câu hỏi:

1. **PIT với JUnit 5 cũ:** JavaTorrent (JUnit Platform 1.0.2) và threeten-jpa (1.1.0) báo `B4_PIT_FAILED`, vì `pitest-junit5-plugin` 1.2.1 trong `tools.lock.json` không chạy với JUnit Platform cũ như vậy. Muốn sửa thì phải thêm một bản plugin cũ hơn vào `tools.lock.json` (dùng khi Platform < 1.x), hoặc chấp nhận không đo mutation cho các project này. JaCoCo và tsDetect vẫn đo bình thường.
2. **Thử lại khi baseline quá thời gian:** `acadia` tốn 46 phút cho 3 lần thử baseline quá giờ, trong khi một test treo gần như chắc chắn sẽ treo lại. Có nên dừng ngay sau lần quá giờ đầu tiên của baseline không?
3. **VALID nhưng test yếu:** có nên cảnh báo khi một bộ test VALID có line < 30% hoặc mutation = 0% không (thêm một dòng `[!]` trước câu hỏi ghi file)?

## 7. Còn lại

- 10 repo còn lại của `validation-set.json`: chưa chạy (Chính chọn chạy 10 repo).
- Chạy lại vài repo dưới dạng bản clone với `--project`, để kiểm đường ghi file trên repo thật.
- Ollama: chưa có trên máy.
