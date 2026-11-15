# Xử lý sự cố

<!-- Sinh từ ErrorCatalog (tests/Arrow.Business.Tests/Common/TroubleshootingDocTests.cs). Đừng sửa tay: sửa ErrorCatalog.vi.json, rồi chạy test với ARROW_UPDATE_DOCS=1. -->

Mỗi lỗi ARROW in ra có dạng `[X] MÃ_LỖI · thông điệp`, kèm `→ Cách sửa: …` và đường dẫn file log khi có. Trang này liệt kê mọi mã lỗi.

**Xem thêm chi tiết:**
- `arrow doctor`: kiểm tra JDK, Maven/Gradle, java-helper, công cụ đo và model.
- `arrow show <run>` và `arrow show <run> <class>`: kết quả từng khối, từng vòng sửa của một run; `arrow history` liệt kê các run.
- Thư mục của run: `ARROW_HOME/runs/<run>/` (prompt, câu trả lời của model, log build). `ARROW_HOME` mặc định là `%LOCALAPPDATA%\arrow` trên Windows; trên Linux/macOS là `$XDG_DATA_HOME/arrow`, không có biến đó thì `~/.arrow`.
- Log của ứng dụng: `ARROW_HOME/logs/`.

## Sự cố thường gặp không có mã lỗi

### Windows SmartScreen chặn `arrow.exe`

Bản chạy độc lập chưa được ký mã. Chọn **More info → Run anyway**, hoặc cài bằng `dotnet tool install -g ArrowDev`.

### macOS báo "cannot be opened because the developer cannot be verified"

File tải về bị gắn cờ quarantine. Chạy `xattr -d com.apple.quarantine ./arrow` trong thư mục giải nén.

### Maven hoặc Gradle không tải được dependency sau proxy

ARROW dùng đúng Maven/Gradle của máy, nên cấu hình proxy giống như khi build bình thường: `~/.m2/settings.xml` cho Maven, `~/.gradle/gradle.properties` cho Gradle. Kiểm tra bằng `mvn -q dependency:resolve` trong project.

### Đường dẫn quá dài trên Windows

Bật `LongPathsEnabled`, hoặc đặt `ARROW_HOME` ở đường dẫn ngắn (`arrow --home D:\arrow …` hoặc biến môi trường `ARROW_HOME`). `arrow doctor` cảnh báo khi chưa bật.

## Khối 1: phân tích project

Trước khi gọi model: tải hoặc chép project, tìm class, build và chạy test sẵn có (baseline). Chưa tốn token.

### `B1_BUILD_ROOT_NOT_FOUND`

**Nguyên nhân:** Không tìm thấy file build Maven hay Gradle trong project.

**Cách sửa:** Chạy trong thư mục project Maven/Gradle, hoặc dùng --project <thư mục>.

### `B1_URL_HAS_CREDENTIALS`

**Nguyên nhân:** URL của repo có thông tin đăng nhập (user:token@).

**Cách sửa:** Bỏ thông tin đăng nhập khỏi URL; dùng credential helper của git.

### `B1_AUTH_REQUIRED`

**Nguyên nhân:** Repo cần đăng nhập mà git không đăng nhập được.

**Cách sửa:** Đăng nhập GitHub bằng "gh auth login" hoặc Git Credential Manager.

### `B1_CLONE_FAILED`

**Nguyên nhân:** Không tải được repo vì lỗi mạng (đã thử 3 lần).

**Cách sửa:** Kiểm tra mạng và proxy (git config --global http.proxy).

### `B1_COMMIT_MISSING`

**Nguyên nhân:** Không tìm thấy ref hoặc commit `<ref>`.

**Cách sửa:** Kiểm tra tên nhánh hoặc tag; dùng SHA đầy đủ.

### `B1_CUT_MISSING`

**Nguyên nhân:** Không tìm thấy class `<query>`.

**Cách sửa:** Dùng FQN hoặc đường dẫn file .java; xem các tên gần giống ở trên.

### `B1_CUT_AMBIGUOUS`

**Nguyên nhân:** Có nhiều class tên `<query>`, không chọn được class nào.

**Cách sửa:** Dùng FQN, ví dụ com.acme.shop.OrderService.

### `B1_CUT_NOT_TESTABLE`

**Nguyên nhân:** `<query>` là interface, annotation, class lồng, hoặc nằm trong thư mục test.

**Cách sửa:** Chọn một class cài đặt.

### `B1_MODULE_MISMATCH`

**Nguyên nhân:** Không xác định được module chứa CUT: module root `<module_root>`, build root `<build_root>`.

**Cách sửa:** Kiểm tra hai đường dẫn trên; dùng "arrow inspect" để xem cách ARROW hiểu project.

### `B1_BUILD_TOOL_MISSING`

**Nguyên nhân:** Project không có wrapper, và máy không có Maven hay Gradle.

**Cách sửa:** Cài Maven hoặc Gradle, hoặc chạy "arrow setup".

### `B1_JDK_MISSING`

**Nguyên nhân:** Máy không có JDK nào dùng được.

**Cách sửa:** Cài JDK `<jdk>`, rồi chạy "arrow setup".

### `B1_DEPENDENCY_RESOLUTION`

**Nguyên nhân:** Không tải được dependency của project.

**Cách sửa:** Chạy "mvn -q dependency:resolve" để xem lỗi; kiểm tra settings.xml và proxy.

### `B1_BASELINE_BUILD_FAILED`

**Nguyên nhân:** Không JDK nào biên dịch được project.

**Cách sửa:** Xem lỗi đầu tiên ở trên; chạy "mvn -q test-compile" trong project để xem lỗi đầy đủ.

### `B1_BASELINE_TIMEOUT`

**Nguyên nhân:** Build hoặc chạy test của project quá thời gian cho phép (đã thử 3 lần).

**Cách sửa:** Tăng verification.step_timeout_seconds.

### `B1_TEST_NAME_EXHAUSTED`

**Nguyên nhân:** Đã có đủ các file từ _ArrowTest tới _Arrow99Test cho class này.

**Cách sửa:** Xoá bớt các file test cũ do ARROW sinh.

### `B1_WORKSPACE`

**Nguyên nhân:** Không chép hay xoá được bản sao làm việc (đĩa, quyền, đường dẫn quá dài).

**Cách sửa:** Kiểm tra dung lượng trống; đặt ARROW_HOME ở đường dẫn ngắn hơn.


## Khối 2: sinh test và gọi model

Lỗi khi gọi nhà cung cấp LLM hoặc khi câu trả lời không dùng được.

### `B2_INVALID_OUTPUT`

**Nguyên nhân:** Model không trả về mã test dùng được.

**Cách sửa:** Chạy lại, hoặc đổi model.

### `B2_TEMPLATE_INVALID`

**Nguyên nhân:** Template prompt không hợp lệ: thiếu hoặc thừa placeholder, sai id, hoặc file ghi đè sai.

**Cách sửa:** Sửa hoặc xoá thư mục prompts.override_dir.

### `B2_LLM_UNAVAILABLE`

**Nguyên nhân:** Không gọi được model: mạng hoặc provider đang lỗi.

**Cách sửa:** Chạy lại sau bằng "arrow gen --resume `<run_id>`".

### `B2_LLM_CONFIG`

**Nguyên nhân:** Cấu hình model không dùng được (khoá, tài khoản, base_url, model_id hoặc tham số).

**Cách sửa:** Kiểm tra cấu hình bằng "arrow models test", rồi chạy tiếp bằng "arrow gen --resume `<run_id>`".

### `B2_WRONG_MODEL`

**Nguyên nhân:** Endpoint trả về model khác: `<actual>` thay vì `<expected>`.

**Cách sửa:** Kiểm tra model_id, hoặc bỏ expected_model_id_prefix.

### `B2_TOKEN_BUDGET`

**Nguyên nhân:** Đã dùng hết ngân sách token (--max-tokens).

**Cách sửa:** Tăng ngân sách rồi chạy tiếp bằng "arrow gen --resume `<run_id>`".


## Khối 3: xác minh và sửa

Lỗi hạ tầng khi build hoặc chạy test sinh ra; ARROW tự thử lại trước khi báo.

### `B3_TIMEOUT`

**Nguyên nhân:** Một bước xác minh quá thời gian cho phép (đã thử 3 lần).

**Cách sửa:** Test sinh ra có thể bị treo (vòng lặp vô hạn, chờ mạng). Tăng verification.step_timeout_seconds hoặc chạy lại.

### `B3_DEPENDENCY`

**Nguyên nhân:** Không tải được dependency khi xác minh test.

**Cách sửa:** Kiểm tra mạng và settings.xml.

### `B3_TOOL_ERROR`

**Nguyên nhân:** Maven, Gradle hoặc JVM không chạy được, hoặc bị dừng giữa chừng.

**Cách sửa:** Xem log; chạy "arrow doctor".

### `B3_WORKSPACE`

**Nguyên nhân:** Không tạo hay xoá được bản sao để xác minh (đĩa, quyền).

**Cách sửa:** Chạy "arrow clean --work"; kiểm tra dung lượng trống.


## Khối 4: đo chất lượng

Một công cụ đo lỗi không làm bộ test hết VALID; chỉ thiếu chỉ số đó.

### `B4_SUITE_COMPILE`

**Nguyên nhân:** Không biên dịch được bộ test trong bản sao dùng để đo (đã thử 3 lần).

**Cách sửa:** Lỗi môi trường; chạy lại. Nếu vẫn lỗi thì báo lỗi kèm log.

### `B4_JACOCO_FAILED`

**Nguyên nhân:** Không có dữ liệu coverage của class cần test.

**Cách sửa:** Project có thể chạy test bằng JVM riêng không nhận JAVA_TOOL_OPTIONS. Xem log.

### `B4_PIT_FAILED`

**Nguyên nhân:** PIT lỗi, hoặc máy không có JDK 11 trở lên.

**Cách sửa:** Cài JDK 11 trở lên; xem 20 dòng cuối của log.

### `B4_PIT_SCOPE`

**Nguyên nhân:** PIT tạo mutant ngoài class cần test và các class lồng của nó.

**Cách sửa:** Đây là lỗi cấu hình của ARROW. Báo lỗi kèm log.

### `B4_TSDETECT_FAILED`

**Nguyên nhân:** tsDetect lỗi.

**Cách sửa:** Xem log.


## Ghi file vào project

ARROW không ghi khi có điều gì không chắc chắn.

### `W_TARGET_OUTSIDE_PROJECT`

**Nguyên nhân:** Đường dẫn đích nằm ngoài project hoặc ngoài thư mục test (symlink, junction, ".."). Không ghi file.

**Cách sửa:** Kiểm tra symlink trong project; báo lỗi kèm log.

### `W_PROJECT_CHANGED`

**Nguyên nhân:** File của class cần test đã thay đổi kể từ lúc chạy.

**Cách sửa:** Chạy lại "arrow gen" cho class đó.

### `W_REVERIFY_FAILED`

**Nguyên nhân:** Bản đổi tên _Arrow<k>Test không còn VALID.

**Cách sửa:** Bản gốc vẫn nằm trong runs/. Chạy lại "arrow gen".

### `W_TARGET_EXISTS`

**Nguyên nhân:** File đích xuất hiện trong lúc ghi, hai lần liền.

**Cách sửa:** Chạy lại.

### `W_IO`

**Nguyên nhân:** Không ghi được file test (quyền, đĩa đầy, hoặc file bị khoá).

**Cách sửa:** Kiểm tra quyền ghi thư mục test, hoặc đóng IDE đang khoá file.


## Cấu hình, khoá và công cụ

Cấu hình nằm trong ARROW_HOME/config.json và .arrow.json của project; xem bằng "arrow config list".

### `CFG_HOME_PATH`

**Nguyên nhân:** ARROW_HOME có dấu cách hoặc ký tự không phải ASCII: `<path>`.

**Cách sửa:** Đặt biến môi trường ARROW_HOME tới một thư mục khác, ví dụ D:\arrow.

### `CFG_PARSE`

**Nguyên nhân:** File cấu hình `<file>` không phải JSON hợp lệ (dòng `<line>`, cột `<column>`).

**Cách sửa:** Sửa file ở vị trí trên; "arrow config path" in đường dẫn các file cấu hình.

### `CFG_INVALID`

**Nguyên nhân:** Giá trị cấu hình không hợp lệ: `<key>` = `<value>` (từ `<origin>`).

**Cách sửa:** Sửa bằng "arrow config set `<key>` <giá trị>" hoặc trong file cấu hình ở trên.

### `CFG_NEWER_SCHEMA`

**Nguyên nhân:** File cấu hình `<file>` do một bản ARROW mới hơn tạo ra.

**Cách sửa:** Cập nhật ARROW.

### `CFG_SECRET_IN_PROJECT`

**Nguyên nhân:** File .arrow.json của project có khoá API.

**Cách sửa:** Xoá khoá khỏi file, rồi dùng "arrow secrets set <ref>".

### `CFG_NO_MODEL`

**Nguyên nhân:** Chưa chọn model.

**Cách sửa:** Chạy "arrow setup", hoặc dùng --model <tên>.

### `CFG_SECRET_MISSING`

**Nguyên nhân:** Provider `<provider>` cần khoá API mà không tìm thấy khoá.

**Cách sửa:** Chạy "arrow secrets set `<ref>`", hoặc đặt biến môi trường `<env_var>`.

### `CFG_TOOL_MISSING`

**Nguyên nhân:** Thiếu công cụ `<tool>` và không tải được.

**Cách sửa:** Chạy "arrow tools install" khi có mạng.

### `CFG_TOOL_CHECKSUM`

**Nguyên nhân:** File `<tool>` tải về sai sha256.

**Cách sửa:** Thử lại; nếu vẫn sai thì kiểm tra proxy hoặc ARROW_MAVEN_MIRROR.

### `CFG_DISK_FULL`

**Nguyên nhân:** Không đủ chỗ trống trong ARROW_HOME (cần ít nhất 2 GB).

**Cách sửa:** Chạy "arrow clean --work --runs".


## java-helper

Tiến trình Java nhỏ ARROW dùng để đọc mã Java; cần JDK 17 trở lên.

### `JH_JDK_MISSING`

**Nguyên nhân:** java-helper cần JDK 17 trở lên mà máy không có.

**Cách sửa:** Cài JDK 17 hoặc mới hơn, rồi chạy "arrow setup".

### `JH_START_FAILED`

**Nguyên nhân:** java-helper không khởi động được, hoặc dừng bất thường hai lần liền.

**Cách sửa:** Xem log; chạy "arrow doctor".

### `JH_TIMEOUT`

**Nguyên nhân:** Lệnh java-helper quá thời gian cho phép hai lần liền.

**Cách sửa:** Báo lỗi kèm log.

### `JH_VERSION_MISMATCH`

**Nguyên nhân:** java-helper sai phiên bản: cần `<expected>`, có `<actual>`.

**Cách sửa:** Xoá ARROW_HOME/tools/java-helper-*.jar rồi chạy lại.

### `JH_INPUT_TOO_LARGE`

**Nguyên nhân:** Mã gửi cho java-helper lớn hơn 2 MB.


## Lệnh arrow gen

### `GEN_NO_TARGETS`

**Nguyên nhân:** Không còn class nào để chạy.

**Cách sửa:** Kiểm tra tên class hoặc package.

### `GEN_RESUME_MISMATCH`

**Nguyên nhân:** Project đã thay đổi kể từ run `<run_id>`.

**Cách sửa:** Chạy "arrow gen" mới, không dùng --resume.

### `GEN_RESUME_NOT_FOUND`

**Nguyên nhân:** Không có run `<run_id>`, hoặc run đó thuộc project khác.

**Cách sửa:** Xem các run bằng "arrow history".


## Dòng lệnh

### `CLI_ARGS`

**Nguyên nhân:** Tham số sai hoặc thiếu.

**Cách sửa:** Gõ "`<help_command>`" để xem cách dùng.

### `CLI_NO_TTY`

**Nguyên nhân:** Lệnh này cần bàn phím, nhưng đầu vào đang bị chuyển hướng.

**Cách sửa:** Chạy lệnh trong một terminal tương tác.

### `CLI_INTERNAL`

**Nguyên nhân:** Lỗi nội bộ của ARROW.

**Cách sửa:** Báo lỗi kèm file log: `<log_path>`.
