# ARROW Developer CLI

`arrow` sinh unit test cho một class Java bằng LLM, rồi chỉ giữ lại test thật sự chạy được trên project của bạn:

1. **Phân tích project** Maven hoặc Gradle (thư mục trên máy hoặc repo GitHub): build tool, JDK, test framework, dependency, và chạy test sẵn có hai lần để biết test nào vốn đã pass.
2. **Sinh test** bằng LLM, với ngữ cảnh lấy từ project.
3. **Xác minh trên build thật** qua 5 bước: biên dịch, test được tìm thấy, chạy không lỗi, assertion đúng, không làm hỏng test sẵn có. Lỗi thì **sửa tự động** (Adaptive Repair, mặc định tối đa 5 vòng).
4. **Đo chất lượng:** coverage (JaCoCo), mutation score (PIT), test smell (tsDetect).
5. **Hỏi trước khi ghi** `<Class>_ArrowTest.java` vào project. Không bao giờ ghi đè: nếu tên đã có thì dùng `<Class>_Arrow2Test`, …

Hỗ trợ JUnit 4, JUnit 5, TestNG; Java 8, 11, 17, 21; project nhiều module.

## Cài đặt

**Cách 1: file chạy độc lập** (không cần cài .NET). Tải từ [GitHub Releases](https://github.com/Duc-Chinh1907/Arrow_Developer/releases) file của hệ điều hành của bạn, giải nén, rồi đặt `arrow` (`arrow.exe`) vào một thư mục trong `PATH`:

| Hệ điều hành | File |
|---|---|
| Windows x64 | `arrow-<phiên bản>-win-x64.zip` |
| Linux x64 | `arrow-<phiên bản>-linux-x64.tar.gz` |
| macOS Apple Silicon | `arrow-<phiên bản>-osx-arm64.tar.gz` |

Kiểm tra file tải về bằng `SHA256SUMS.txt` đi kèm. File chưa được ký mã: Windows SmartScreen có thể cảnh báo (chọn **More info → Run anyway**); trên macOS chạy `xattr -d com.apple.quarantine ./arrow` một lần.

**Cách 2: `dotnet tool`** (cần .NET 10 runtime). Tải file `ArrowDev.<phiên bản>.nupkg` từ GitHub Releases vào một thư mục, rồi:

```bash
dotnet tool install -g ArrowDev --add-source <thư mục chứa file .nupkg>
```

Kiểm tra: `arrow --version`.

## Yêu cầu

- **JDK** đúng phiên bản project khai báo (8, 11, 17 hoặc 21), và **một JDK 17 trở lên** cho java-helper (thành phần đọc mã Java của ARROW). ARROW tự tìm JDK qua `JAVA_HOME`, `JAVA_HOME_<n>_X64`, `PATH` và các thư mục cài đặt thường gặp.
- **Maven hoặc Gradle** trên máy, hoặc project có wrapper (`mvnw`, `gradlew`).
- **git**, chỉ khi dùng `--repo`.
- **Một nhà cung cấp LLM:** OpenAI, OpenRouter, Anthropic, một dịch vụ tương thích OpenAI, hoặc Ollama chạy trên máy.

`arrow doctor` kiểm tra tất cả những thứ trên.

## Bắt đầu

```bash
arrow setup
```

`setup` đi qua 5 bước: chọn nhà cung cấp và nhập khoá API (ẩn khi gõ), chọn model và gọi thử, đồng ý điều khoản quyền riêng tư, kiểm JDK và Maven/Gradle, tải công cụ đo. Cuối cùng nó chạy `arrow doctor`.

Sinh test cho một class, trong thư mục project:

```bash
cd my-project
arrow gen OrderService
```

ARROW in tiến độ từng khối, bảng kết quả, rồi hỏi `Ghi …OrderService_ArrowTest.java? (Y/n)`.

Một vài cách dùng khác:

```bash
arrow gen com.myshop.OrderService Cart --jobs 2        # nhiều class, chạy song song
arrow gen --package com.myshop.service                 # mọi class cụ thể của một package
arrow gen OrderService --repo https://github.com/<owner>/<repo> --ref <sha đầy đủ>   # repo GitHub, test chỉ lưu trong ARROW_HOME/runs
arrow gen OrderService --dry-run                       # chạy đủ nhưng không ghi vào project
arrow gen OrderService --no-metrics --rounds 3         # bỏ đo chất lượng, tối đa 3 vòng sửa
arrow gen OrderService --no-pit                        # không đo mutation (PIT, bước đo lâu nhất)
arrow gen OrderService --max-tokens 50000              # ngân sách token của cả run
arrow inspect OrderService                             # chỉ phân tích project, không gọi model
arrow history                                          # các run đã chạy
arrow show <run> OrderService                          # chi tiết từng vòng sửa
```

Run bị ngắt (Ctrl+C, mất mạng, hết ngân sách token) chạy tiếp được: `arrow gen` tự đề nghị, hoặc `arrow gen --resume <run>`. Lượt gọi model đã có được phát lại, không tốn tiền lại.

Lần chạy sau trên cùng project (project, build và JDK không đổi, trong 7 ngày) dùng lại kết quả chạy test sẵn có đã đo, nên nhanh hơn lần đầu. `--fresh-baseline` buộc đo lại.

### Phiên tương tác

Gõ `arrow` không kèm lệnh để mở phiên:

```
 ARROW · LLM unit test generation for Java · v0.2.0
 project  my-project  (Maven · Java 17 · JUnit 5)
 model    gpt-x · api.example.com · repair adaptive N=5
arrow> /gen
```

`/gen` không kèm class sẽ mở ô chọn class. Các lệnh khác: `/inspect`, `/history`, `/show`, `/model` (đổi model cho phiên), `/project`, `/setup`, `/doctor`, `/config`, `/tools`, `/help`, `/exit`. Tab gợi ý lệnh, tuỳ chọn và tên class; Ctrl+C huỷ việc đang chạy mà không thoát phiên.

## Đọc kết quả

- **VALID** nghĩa là bộ test qua đủ 5 bước xác minh. Nó **không** có nghĩa là test kiểm tra tốt: hãy xem line, branch, mutation trong bảng kết quả. ARROW cảnh báo khi một bộ test VALID có line dưới 30% hoặc mutation 0%.
- Các kết quả khác: `COMPILE`, `DISCOVERY`, `RUNTIME`, `ASSERTION`, `REGRESSION` (test sinh ra vẫn lỗi ở bước đó sau các vòng sửa), `BLOCKED` (project không dùng được, ví dụ không build được), `INFRA` (lỗi hạ tầng, ví dụ quá thời gian).
- Mỗi lỗi có mã và gợi ý cách sửa. Danh sách đầy đủ: [docs/troubleshooting.md](docs/troubleshooting.md).

## Quyền riêng tư

Mỗi lượt gọi model gửi tới nhà cung cấp bạn chọn:
- mã nguồn của class cần test;
- thông tin project: tên dependency, đường dẫn trong project, phiên bản Java và test framework;
- mã test sinh ra và lỗi build/test của nó khi sửa.

ARROW **không** gửi khoá API, biến môi trường, tên máy hay tên người dùng. Với Ollama chạy trên máy, mã không rời khỏi máy.

Khoá API chỉ nằm trong kho khoá của hệ điều hành (DPAPI trên Windows, file quyền 600 ở nơi khác) hoặc biến môi trường `ARROW_SECRET_<TÊN>`, không bao giờ nằm trong file cấu hình hay log. Khi chạy từ 5 class trở lên (`run.confirm_cost_from_classes`), ARROW ước tính số token và chi phí rồi hỏi trước.

## Dữ liệu trên máy

Mọi thứ ARROW tạo ra nằm trong `ARROW_HOME`: mặc định `%LOCALAPPDATA%\arrow` trên Windows; trên Linux/macOS là `$XDG_DATA_HOME/arrow`, không có biến đó thì `~/.arrow`. Đặt chỗ khác bằng biến môi trường `ARROW_HOME` hoặc `--home`. Trong đó có `config.json` (`arrow config list`), các run (`runs/`), công cụ đo (`tools/`) và log (`logs/`).

ARROW không sửa gì trong project ngoài file test bạn đồng ý ghi: mọi lần build đều chạy trên bản sao.

## Build từ mã nguồn

Cần .NET SDK 10.0.404 trở lên (dải 10.0.4xx), JDK 17 và Maven.

```bash
mvn -q -f tools/java-helper/pom.xml package   # trước: java-helper.jar được nhúng vào bản build
dotnet build ARROW.slnx
dotnet test --solution ARROW.slnx
```

Tài liệu phát triển: kế hoạch [docs/PLAN_CODE.vi.md](docs/PLAN_CODE.vi.md), kiến trúc [docs/ARCHITECTURE.vi.md](docs/ARCHITECTURE.vi.md), đặc tả [docs/specs/](docs/specs/), kết quả chạy thử trên repo thật [docs/validation.md](docs/validation.md), thay đổi [CHANGELOG.md](CHANGELOG.md).

## Giấy phép

[MIT](LICENSE). ARROW Developer CLI là bản sản phẩm của framework nghiên cứu ARROW, nhóm SET490_G2.
