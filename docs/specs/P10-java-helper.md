# P10. java-helper

**Mục đích:** java-helper là một chương trình Java nhỏ, làm mọi việc cần **phân tích mã Java ở mức cú pháp (AST)** hoặc **đọc class trong jar**, những việc C# không làm tốt:
- kiểm tra và chuẩn hoá mã do model trả về;
- tính `code_hash`;
- đếm assertion;
- tìm method chứa dòng lỗi;
- ghép method ở mức 3;
- lấy API và mã nguồn method cho `extra_context`;
- liệt kê class của project.

**Lớp:** chương trình Java ở `tools/java-helper/`. Phía C# gọi qua `JavaHelperClient` (`Arrow.DataAccess`).

**Nguồn gốc:** dùng lại `ARROW_Framework/tools/java-helper` (phiên bản 1.0.0, 12 lệnh xử lý cộng `shutdown`, có test). Sản phẩm thêm lệnh `list_classes`, mở rộng `count_assertions` theo R03 §3.6 bản mới và nâng lên phiên bản 1.1.0.

## 1. Phạm vi

| Có | Không |
|---|---|
| Phân tích cú pháp mã Java 8–21 (JavaParser) | Biên dịch hay chạy mã |
| Sửa mã dựa trên vị trí trong văn bản gốc, giữ mọi ký tự khác | Định dạng lại mã |
| Đọc chữ ký class trong jar bằng reflection, **không khởi tạo class** | Chạy static initializer của thư viện |
| Một tiến trình sống suốt một run, nhận nhiều lệnh | Server mạng, nhiều client cùng lúc |

## 2. Công nghệ và build

| Mục | Giá trị |
|---|---|
| Ngôn ngữ, mức | Java 17 (`maven.compiler.release = 17`) |
| Thư viện | `com.github.javaparser:javaparser-core` 3.26.2, `com.google.code.gson:gson` 2.11.0 |
| Test | JUnit Jupiter 5.10.2 |
| Build | `mvn -q -f tools/java-helper/pom.xml package` → `target/java-helper.jar` (một file, gồm cả thư viện) |
| Mức ngôn ngữ của parser | `ParserConfiguration.LanguageLevel.JAVA_21` để đọc được mọi cú pháp tới Java 21 (record, sealed, pattern matching, text block) |
| Phiên bản | Hằng số `Main.VERSION`; bản sản phẩm là `1.1.0` |

**Phát hành:**
- `java-helper.jar` được nhúng vào `Arrow.DataAccess` (EmbeddedResource).
- Lần chạy đầu, jar được giải nén ra `ARROW_HOME/tools/java-helper-<version>.jar`. Đã có thì kiểm tra sha256, sai thì giải nén lại.
- sha256 của jar được ghi vào `tools.lock.json` lúc build (CI tính).

## 3. Giao thức

### 3.1 Khởi động

```
<java 17+> -Xss8m -Xmx512m -Dfile.encoding=UTF-8 -jar <ARROW_HOME>/tools/java-helper-<ver>.jar
```

- **JDK:** JDK nhỏ nhất có major ≥ 17 trong `java.jdk_homes`. Không có thì báo `JH_JDK_MISSING`.
- **`-Xss8m`:** tránh `StackOverflowError` khi phân tích file có biểu thức lồng sâu.

### 3.2 Trao đổi

- **Vào và ra:** UTF-8, **mỗi dòng một đối tượng JSON**.
- **Yêu cầu:** `{"id": <số>, "command": "<tên>", "args": { … }}`.
- **Trả lời** (đúng một dòng cho mỗi yêu cầu, cùng `id`):
  - `{"id": <số>, "ok": true, "result": { … }}`, hoặc
  - `{"id": <số>, "ok": false, "error": "<Loại>: <thông điệp>"}`.
- Dòng vào không phải JSON: trả `{"id": null, "ok": false, "error": "…"}`.
- Lệnh lỗi **không bao giờ** làm chương trình dừng. Mọi `RuntimeException` và `StackOverflowError` đều được bắt rồi trả về `ok: false`.
- Lệnh `shutdown` trả lời rồi kết thúc. Hết stdin cũng kết thúc.
- Chỉ stdout là kênh trả lời. Log và cảnh báo ghi ra **stderr**; phía C# đọc stderr vào log của run.
- Mỗi lần chỉ xử lý **một lệnh**, theo thứ tự nhận.

### 3.3 Quy ước chung của lệnh

- **`code`** là mã nguồn đầy đủ của một file `.java`, dạng chuỗi.
- **Lệnh có `parsed` trong kết quả:** mã không parse được thì trả `ok: true`, `parsed: false`, các trường khác rỗng hoặc `null`. Không trả `ok: false` trong trường hợp này.
- **Dòng** đánh số từ 1.
- **Danh sách** giữ thứ tự xuất hiện trong mã.

## 4. Các lệnh

### 4.1 `ping`

| | |
|---|---|
| Vào | `{}` |
| Ra | `{ "version": "1.1.0" }` |
| Dùng ở | `JavaHelperClient` khi khởi động; `arrow doctor` |

### 4.2 `parse`

| | |
|---|---|
| Vào | `{ "code": "…" }` |
| Ra | `{ "parsed": true, "error": null, "package": "com.acme", "top_level_types": [ { "name": "OrderService_ArrowTest", "public": true } ], "test_method_count": 6 }` |
| Dùng ở | P02 §5.2: phân loại `parse_error`, `multiple_classes` |

### 4.3 `rename_class`

| | |
|---|---|
| Vào | `{ "code": "…", "new_name": "OrderService_ArrowTest", "package": "com.acme" }`. `package` tuỳ chọn; chuỗi rỗng = không có package |
| Ra | `{ "code": "…", "old_name": "OrderServiceTest", "actions": ["package: added", "class: OrderServiceTest → OrderService_ArrowTest"] }` |
| Hành vi | Đặt hoặc sửa `package`. Đổi tên class chính và **mọi chỗ tham chiếu** (constructor, kiểu của field, biến, tham số, lời gọi static, `new`). Thay trực tiếp trên văn bản gốc theo vị trí lấy từ AST, nên định dạng và comment giữ nguyên |
| Dùng ở | P02 §5.3; P06 khi phải đổi sang `_Arrow<k>Test` |

### 4.4 `strip_comments_hash`

| | |
|---|---|
| Vào | `{ "code": "…" }` |
| Ra | `{ "parsed": true, "normalized": "…", "code_hash": "sha256:<64 hex>" }` |
| Hành vi | Bỏ mọi comment; thu mọi chuỗi khoảng trắng (kể cả xuống dòng) thành một dấu cách, trừ khoảng trắng nằm trong chuỗi ký tự; bỏ khoảng trắng đầu và cuối. Hai đoạn mã chỉ khác comment, xuống dòng hay thụt lề cho **cùng hash** |
| Dùng ở | P02 §5.5 (`code_hash`); P04 phát hiện mã trùng |

### 4.5 `shrink_source`

| | |
|---|---|
| Vào | `{ "code": "…", "max_chars": 48000 }` |
| Ra | `{ "code": "…", "truncation": "none" \| "comments_removed" \| "signatures_only" }` |
| Hành vi | Không quá `max_chars` thì giữ nguyên. Quá thì bỏ comment và dòng trống. Vẫn quá thì giữ mọi khai báo và chữ ký constructor/method; thân method giữ lần lượt theo thứ tự trong file tới khi đủ giới hạn; thân còn lại thay bằng `{ ... }` |
| Dùng ở | P02 §3.3 |

### 4.6 `splice_methods`

| | |
|---|---|
| Vào | `{ "base": "<mã checkpoint>", "response": "<mã model trả về>", "methods": ["totalPriceAddsTax", "rejectsNull"] }` |
| Ra | `{ "code": "…", "replaced": ["totalPriceAddsTax"], "dropped": ["rejectsNull"], "added_imports": ["java.math.BigDecimal"], "added_fields": [], "added_methods": ["newOrder"] }` |
| Hành vi | Mỗi method trong `methods` (mọi overload cùng tên) của `base` được thay bằng method cùng tên trong `response`. Method có trong `methods` mà `response` không có thì **bị bỏ** khỏi `base` (`dropped`). Import, field và helper method mới trong `response` được **thêm** vào. Mọi ký tự khác của `base` giữ nguyên |
| Dùng ở | P02 §5.4 (mức 3) |

### 4.7 `list_test_methods`

| | |
|---|---|
| Vào | `{ "code": "…" }` |
| Ra | `{ "parsed": true, "methods": ["addsItem", "rejectsNull"] }` |
| Hành vi | Method có `@Test` (JUnit 4, JUnit 5, TestNG), `@ParameterizedTest`, `@RepeatedTest`, `@TestFactory`, `@TestTemplate` |
| Dùng ở | P02 (`test_method_count`); P04 (`dropped_methods`); P05 (mật độ smell) |

### 4.8 `count_assertions`

| | |
|---|---|
| Vào | `{ "code": "…" }` |
| Ra | `{ "parsed": true, "total": 11, "per_method": { "addsItem": 3, "rejectsNull": 1 } }` |
| Tính | Theo R03 §3.6 (bổ sung 15/10/2026):<br>• `assert*` của JUnit/TestNG; `assertThat` (AssertJ, Hamcrest);<br>• `@Test(expected = …)`, `expectedExceptions`; `fail()` trong `try`;<br>• Mockito: `verify(…)`, kể cả `inOrder.verify(…)`; `verifyNoInteractions`, `verifyNoMoreInteractions`, `verifyZeroInteractions`;<br>• kiểu BDD: `then(mock).should…()` và `then(actual).is…()`;<br>• MockMvc `andExpect(…)`;<br>• `thrown.expect…(…)` của `ExpectedException` (JUnit 4);<br>• mỗi kiểm tra trong chuỗi `then()`/`expect()` của RestAssured |
| Không tính | Assertion tầm thường: `assertTrue(true)`, `assertEquals(x, x)`, `assertNull(null)`, `assertNotNull(new X())`; `assertThat()` không tham số |
| Dùng ở | P04: `assertion_count`, tiêu chí thứ 3 khi so checkpoint |

### 4.9 `method_at_line`

| | |
|---|---|
| Vào | `{ "code": "…", "lines": [13, 42] }` |
| Ra | `{ "parsed": true, "methods": { "13": "addsItem", "42": null } }` |
| Hành vi | Method hoặc constructor **nhỏ nhất** chứa dòng đó; `null` nếu dòng nằm ngoài mọi method (import, field) |
| Dùng ở | P04: `failing_methods` của lỗi COMPILE |

### 4.10 `class_api`

| | |
|---|---|
| Vào | `{ "sources": ["<mã class dự án>", …], "classes": ["com.x.Repo", "org.lib.Client"], "classpath": ["/abs/a.jar", "/abs/target/classes"] }` |
| Ra | `{ "types": [ { "name": "com.x.Repo", "kind": "class", "origin": "source" \| "classpath", "members": ["public Repo(Path root)", "public List<Order> findAll()", "…"] } ], "missing": [ { "name": "org.lib.Gone", "reason": "ClassNotFoundException: …" } ] }` |
| Hành vi | Chữ ký constructor, method và field **không private**. Lấy từ hai nguồn: mã nguồn trong `sources`, và class của thư viện trong `classes` + `classpath`. Class thư viện được nạp bằng `URLClassLoader` với cha là platform class loader, gọi `Class.forName(name, false, loader)` nên **không khởi tạo class**. Vòng đời loader nằm trong lệnh |
| Dùng ở | P04 `extra_context` cho COMPILE |

### 4.11 `method_source`

| | |
|---|---|
| Vào | `{ "code": "…", "names": ["totalPrice", "<init>"] }` |
| Ra | `{ "parsed": true, "methods": [ { "name": "totalPrice", "source": "public long totalPrice(…) { … }" } ] }` |
| Hành vi | Mã nguyên văn của mọi overload có tên đó. `<init>` là constructor |
| Dùng ở | P04 `extra_context` cho RUNTIME (method trong stack trace) |

### 4.12 `called_methods`

| | |
|---|---|
| Vào | `{ "test_code": "…", "test_methods": ["addsItem"], "cut_code": "…" }`. `test_methods` rỗng = mọi method |
| Ra | `{ "parsed": true, "called": ["add", "total"], "methods": [ { "name": "add", "source": "…" } ], "constructors": ["public Cart()"], "fields": ["private final List<Item> items = new ArrayList<>()"] }` |
| Hành vi | Các method của CUT mà các test method đã cho gọi tới (theo tên, trên AST), kèm mã nguồn, constructor và khai báo field của CUT |
| Dùng ở | P04 `extra_context` cho ASSERTION và RUNTIME |

### 4.13 `list_classes` (mới)

| | |
|---|---|
| Vào | `{ "roots": ["/abs/shop-core/src/main/java", "…"], "max_files": 20000 }` |
| Ra | `{ "classes": [ { "fqn": "com.myshop.service.OrderService", "simple_name": "OrderService", "kind": "class", "is_abstract": false, "is_public": true, "path": "/abs/…/OrderService.java", "root": "/abs/shop-core/src/main/java" } ], "errors": [ { "path": "…", "error": "…" } ], "truncated": false }` |
| Hành vi | Duyệt mọi file `.java` trong `roots`. Mỗi file lấy **các kiểu top-level**. `kind` là `class`, `interface`, `enum`, `record` hoặc `annotation`; `fqn` = package + tên. File không parse được ghi vào `errors`, không dừng. Quá `max_files` thì dừng, `truncated: true` |
| Hiệu năng | Chỉ parse phần khai báo: dùng `StaticJavaParser` với `setAttributeComments(false)`. Mục tiêu dưới 10 giây cho 5.000 file |
| Dùng ở | P01 §3.3 `ClassLocator`; P08 §5.3 ô chọn class |

### 4.14 `shutdown`

Vào `{}`. Ra `{ "version": "…" }`, rồi kết thúc chương trình.

## 5. `JavaHelperClient` (C#)

```csharp
public interface IJavaHelper : IAsyncDisposable
{
    Task<string> PingAsync(CancellationToken ct);
    Task<ParseResult> ParseAsync(string code, CancellationToken ct);
    Task<RenameResult> RenameClassAsync(string code, string newName, string? package, CancellationToken ct);
    Task<HashResult> StripCommentsHashAsync(string code, CancellationToken ct);
    Task<ShrinkResult> ShrinkSourceAsync(string code, int maxChars, CancellationToken ct);
    Task<SpliceResult> SpliceMethodsAsync(string baseCode, string response, IReadOnlyList<string> methods, CancellationToken ct);
    Task<IReadOnlyList<string>> ListTestMethodsAsync(string code, CancellationToken ct);
    Task<AssertionCount> CountAssertionsAsync(string code, CancellationToken ct);
    Task<IReadOnlyDictionary<int, string?>> MethodAtLineAsync(string code, IReadOnlyList<int> lines, CancellationToken ct);
    Task<ClassApiResult> ClassApiAsync(IReadOnlyList<string> sources, IReadOnlyList<string> classes, IReadOnlyList<string> classpath, CancellationToken ct);
    Task<IReadOnlyList<MethodSourceEntry>> MethodSourceAsync(string code, IReadOnlyList<string> names, CancellationToken ct);
    Task<CalledMethodsResult> CalledMethodsAsync(string testCode, IReadOnlyList<string> testMethods, string cutCode, CancellationToken ct);
    Task<ListClassesResult> ListClassesAsync(IReadOnlyList<string> roots, int maxFiles, CancellationToken ct);
}
```

**Hành vi:**

| Việc | Quy tắc |
|---|---|
| Khởi động | Lần gọi đầu tiên mới khởi động (lazy). Ngay sau đó gửi `ping`; phiên bản khác bản mong đợi thì báo `JH_VERSION_MISMATCH` |
| Một tiến trình mỗi run | Mọi khối dùng chung một `IJavaHelper` (singleton trong phạm vi run). Gửi lệnh tuần tự qua một `SemaphoreSlim(1)`. Chạy `--jobs` song song thì mỗi job có một tiến trình riêng, tối đa `run.jobs` tiến trình |
| Timeout | 30 s mỗi lệnh, riêng `list_classes` 120 s và `class_api` 60 s. Quá hạn thì giết tiến trình, coi như lỗi tạm thời |
| Tiến trình chết hoặc timeout | Khởi động lại **một lần** rồi gửi lại lệnh đó. Lần thứ hai vẫn lỗi thì báo `JH_START_FAILED` hoặc `JH_TIMEOUT` |
| `ok: false` | Ném `JavaHelperCommandException` kèm `error`. Bên gọi quyết định xử lý (ví dụ khối 2 coi là `parse_error`) |
| Dừng | `DisposeAsync` gửi `shutdown`, chờ 2 s, rồi giết tiến trình nếu còn sống |
| Log | Mỗi lệnh ghi một dòng vào log của run: `command`, độ dài `code`, thời gian, `ok`. **Không ghi nội dung mã**. stderr của java-helper được chép vào log |
| Kích thước | Chuỗi JSON một dòng có thể dài vài MB; đọc bằng `StreamReader.ReadLineAsync` không giới hạn độ dài. Một lệnh có `code` lớn hơn 2 MB thì từ chối trước khi gửi (`JH_INPUT_TOO_LARGE`) |

## 6. Mã lỗi

| Mã | Khi nào | Gợi ý |
|---|---|---|
| `JH_JDK_MISSING` | Không có JDK ≥ 17 | Cài JDK 17 hoặc mới hơn, rồi chạy `arrow setup` |
| `JH_START_FAILED` | Tiến trình không khởi động được, hoặc chết hai lần liền | Xem log; chạy `arrow doctor` |
| `JH_TIMEOUT` | Lệnh quá thời gian hai lần liền | Báo lỗi kèm log |
| `JH_VERSION_MISMATCH` | Jar sai phiên bản | Xoá `ARROW_HOME/tools/java-helper-*.jar` rồi chạy lại |
| `JH_INPUT_TOO_LARGE` | `code` lớn hơn 2 MB | — |

## 7. Kiểm thử

**JUnit trong java-helper** (`CommandsTest`, giữ của `ARROW_Framework` và bổ sung). Mỗi lệnh phải có ít nhất:
- mã hợp lệ;
- mã lỗi cú pháp (`parsed: false`);
- generic, class lồng, record, enum, annotation, lambda, text block (Java 15+), switch biểu thức;
- `rename_class`: tham chiếu trong `new`, kiểu field, lời gọi static, comment giữ nguyên;
- `strip_comments_hash`: hai đoạn chỉ khác comment, thụt lề và xuống dòng cho cùng hash; khác một ký tự trong chuỗi thì khác hash;
- `splice_methods`: thay, bỏ, thêm import, field và helper; ký tự khác giữ nguyên từng byte;
- `count_assertions`: từng loại được tính và không được tính ở Mục 4.8. Riêng các loại thêm ngày 15/10 (`inOrder.verify`, `verifyNo*`, BDD `then(…)`, `andExpect`, `thrown.expect…`) mỗi loại một test. `then()` của RestAssured không bị nhầm với BDD của Mockito;
- `class_api` với jar: class có static initializer ném exception vẫn đọc được chữ ký, vì không khởi tạo;
- `list_classes`: 2 root, có file lỗi cú pháp, có `package-info.java` và `module-info.java` (bỏ qua);
- giao thức: dòng không phải JSON, lệnh không tồn tại, `shutdown`.

**Test C#** (`Arrow.IntegrationTests/JavaHelperTests`):
- 1.000 lệnh liên tiếp trên một tiến trình;
- giết tiến trình giữa chừng, lệnh kế tiếp vẫn thành công sau khi tự khởi động lại;
- timeout;
- sai phiên bản;
- hai job song song, mỗi job một tiến trình;
- `DisposeAsync` không để lại tiến trình `java` nào.

**Nghiệm thu:**
- `mvn verify` của java-helper xanh trên CI.
- `list_classes` và `parse` chạy trên mọi file `.java` của 9 fixture mà không lỗi.
- `list_classes` trên một repo thật khoảng 3.000 file xong dưới 10 giây.
