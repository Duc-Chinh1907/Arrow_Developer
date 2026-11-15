# Nghiệm thu bản phát hành (P11 §9)

## 0.1.0-rc.2 trên máy sạch Windows (15/11/2026)

**Máy:** Windows Sandbox trên Windows 11 Pro. Không có .NET, git, cache Maven hay cấu hình ARROW; chỉ có JDK 17.0.21 (Temurin) và Maven 3.9.16, giải nén từ bản zip chính thức (đã kiểm sha256/sha512), đặt bằng `JAVA_HOME` và `PATH`. Người chạy: Chính.

**Bản thử:** [`v0.1.0-rc.2`](https://github.com/Duc-Chinh1907/Arrow_Developer/releases/tag/v0.1.0-rc.2), file `arrow-0.1.0-rc.2-win-x64.zip`. Model thật: `gpt-6-luna` qua TeamoRouter.

| Bước | Kết quả |
|---|---|
| Kiểm file tải về bằng `SHA256SUMS.txt` (`certutil -hashfile`) | khớp |
| `arrow --version` | `arrow 0.1.0-rc.2 (commit e64dc3c07c0f, build 2026-11-15)` |
| `arrow setup` | gọi thử model OK (2,7 s); bước Java và build tìm đúng JDK 17 và Maven, không có JDK 8/11/21 và Gradle; tải 12 công cụ đo, sha256 khớp; `doctor`: SẴN SÀNG, 5 cảnh báo đúng với máy (không có git, JDK 8/11/21, chưa bật đường dẫn dài) |
| `arrow gen PriceCalculator` trên `maven-verify-junit4` (Java 8) | khối 1 dùng JDK 17 vì máy không có JDK 8, baseline 3 test ×2 trong 46 s; c0 VALID (5 test, 9 assertion, 1,6k token); line, branch, mutation 100%; hỏi rồi ghi `PriceCalculator_ArrowTest.java`; tổng 92,6 s |
| `mvn -q test` trong project | pass |

Góp ý từ phép thử, đã sửa trong 0.1.0:
- Câu hỏi "Tên cho nhà cung cấp này" của `setup` dễ bị nhầm với tên model: nay là "Tên ngắn để gọi nhà cung cấp này trong ARROW, không phải tên model".
- `doctor` ghi JDK còn thiếu là "chỉ cần cho project Java 8", trong khi JDK 17 vẫn build được project Java 8: nay là "nên có cho project Java 8; thiếu thì ARROW thử JDK khác".
- Chữ tiếng Việt chỉ hiện đúng trong `cmd` sau `chcp 65001`; Windows Terminal và PowerShell 7 hiện đúng sẵn.

## Chưa làm

- Máy sạch Ubuntu 24.04 (P11 §9): để sau 0.1.0. Bản linux-x64 và osx-arm64 đã chạy `--version` trên runner của chính hệ điều hành đó trong `release.yml`.
