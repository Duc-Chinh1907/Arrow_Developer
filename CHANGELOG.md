# Changelog

Định dạng theo [Keep a Changelog](https://keepachangelog.com/vi/1.1.0/); phiên bản theo [SemVer](https://semver.org/lang/vi/).

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
