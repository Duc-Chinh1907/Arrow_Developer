# Changelog

Định dạng theo [Keep a Changelog](https://keepachangelog.com/vi/1.1.0/); phiên bản theo [SemVer](https://semver.org/lang/vi/).

## [Chưa phát hành]

### Thêm

- Chọn quy trình sinh test c0 bằng `arrow config set prompts.strategy <tên>`: `zero_shot_project_aware` (mặc định, có cấu hình project), `zero_shot` hoặc `few_shot` (2 ví dụ cố định). Hai quy trình sau không cho model biết test framework của project nên dễ lỗi biên dịch hơn; ARROW cảnh báo khi dùng chúng. `arrow show` và khung đầu phiên tương tác hiện quy trình đã dùng; chạy tiếp một run giữ quy trình cũ.

## [0.2.0] - 2026-11-16

### Thêm

- `arrow gen --no-pit`: không đo mutation bằng PIT (bước đo lâu nhất); JaCoCo và tsDetect vẫn đo. Chạy tiếp một run giữ đúng lựa chọn này.

### Nhanh hơn

- Khối 4 biên dịch bộ test **một lần** cho cả JaCoCo và PIT; trước đây PIT biên dịch lại (8–23 s mỗi class trên repo thật). Số đo không đổi.
- Khối 4 bỏ PIT khi JaCoCo đo được line 0%: test không chạy dòng nào của class thì mọi mutant đều sống.
- Khối 1 **dùng lại baseline** đã đo của lần chạy trước khi project, build và JDK không đổi (tối đa 7 ngày): lần `inspect` hay `gen` thứ hai trên cùng project không chạy lại test sẵn có. `--fresh-baseline` buộc đo lại; `verification.baseline_cache = false` tắt hẳn.

## [0.1.0] - 2026-11-15

Bản đầu tiên. Đã thử trên máy sạch Windows: [`docs/release-check.md`](docs/release-check.md).

### Có trong bản này

- **`arrow gen`**: sinh unit test cho một hoặc nhiều class Java bằng LLM, xác minh 5 bước trên build thật (biên dịch, tìm thấy test, chạy, assertion, regression với test sẵn có), sửa tự động bằng Adaptive Repair (mặc định tối đa 5 vòng), đo JaCoCo, PIT và tsDetect, rồi hỏi trước khi ghi `<Class>_ArrowTest.java` vào project. Không bao giờ ghi đè: tên đã có thì dùng `<Class>_Arrow2Test`, …
- Project Maven hoặc Gradle, thư mục trên máy hoặc repo GitHub (`--repo <url> --ref <sha>`); JUnit 4, JUnit 5, TestNG; Java 8, 11, 17, 21; nhiều module.
- Nhiều class một lần (`--package`, `--jobs`), ước tính chi phí và hỏi trước, ngân sách `--max-tokens`, chạy tiếp run bị ngắt (`--resume`).
- Cảnh báo khi bộ test VALID nhưng kiểm tra rất ít (line < 30% hoặc mutation 0%).
- Phiên tương tác `arrow`: `/gen` với ô chọn class, `/model`, `/project`, lịch sử lệnh, Tab.
- `arrow setup` (model, khoá, quyền riêng tư, JDK và Maven/Gradle, công cụ đo), `arrow doctor`, `arrow inspect`, `arrow history`, `arrow show`, `arrow models`, `arrow config`, `arrow secrets`, `arrow tools`.
- Nhà cung cấp LLM tương thích OpenAI (OpenAI, OpenRouter, TeamoRouter, Ollama…) và Anthropic. Khoá API chỉ nằm trong kho khoá của máy hoặc biến môi trường `ARROW_SECRET_<TÊN>`.
- Hai cách cài: `dotnet tool install -g ArrowDev` (cần .NET 10), hoặc file chạy độc lập cho Windows x64, Linux x64, macOS arm64.

### Đã kiểm trên repo thật (GĐ7)

13 class của 10 repo GitHub: 10 class cho bộ test VALID, 4 class đạt line và mutation từ 50% trở lên. Chi tiết và giới hạn của kết quả: [`docs/validation.md`](docs/validation.md).

### Sau 0.1.0-rc.2

- `setup`: câu hỏi tên nhà cung cấp nói rõ đó không phải tên model.
- `doctor`: JDK còn thiếu là "nên có", vì ARROW thử JDK khác khi thiếu.

## [0.1.0-rc.2] - 2026-11-15

Bản chạy thử thứ hai của 0.1.0: sửa bước kiểm `--version` của `release.yml` trên Windows. Nội dung như mục 0.1.0 bên dưới.

## [0.1.0-rc.1] - 2026-11-15 (không phát hành)

Bản chạy thử của 0.1.0, để kiểm quy trình phát hành (`release.yml`). Nội dung như mục 0.1.0 bên dưới.
