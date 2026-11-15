using System.Text;
using System.Text.RegularExpressions;
using Arrow.Common.Errors;

namespace Arrow.Business.Tests.Common;

/// <summary>
/// <c>docs/troubleshooting.md</c> is generated from <see cref="ErrorCatalog"/> (P11 §8), so that it never lags behind the codes.
/// This test fails when the file differs from the catalog; run it with <c>ARROW_UPDATE_DOCS=1</c> to write the file again.
/// </summary>
public sealed partial class TroubleshootingDocTests
{
    private const string UpdateVariable = "ARROW_UPDATE_DOCS";

    private static readonly (string Prefix, string Title, string About)[] Groups =
    [
        ("B1_", "Khối 1: phân tích project", "Trước khi gọi model: tải hoặc chép project, tìm class, build và chạy test sẵn có (baseline). Chưa tốn token."),
        ("B2_", "Khối 2: sinh test và gọi model", "Lỗi khi gọi nhà cung cấp LLM hoặc khi câu trả lời không dùng được."),
        ("B3_", "Khối 3: xác minh và sửa", "Lỗi hạ tầng khi build hoặc chạy test sinh ra; ARROW tự thử lại trước khi báo."),
        ("B4_", "Khối 4: đo chất lượng", "Một công cụ đo lỗi không làm bộ test hết VALID; chỉ thiếu chỉ số đó."),
        ("W_", "Ghi file vào project", "ARROW không ghi khi có điều gì không chắc chắn."),
        ("CFG_", "Cấu hình, khoá và công cụ", "Cấu hình nằm trong ARROW_HOME/config.json và .arrow.json của project; xem bằng \"arrow config list\"."),
        ("JH_", "java-helper", "Tiến trình Java nhỏ ARROW dùng để đọc mã Java; cần JDK 17 trở lên."),
        ("GEN_", "Lệnh arrow gen", ""),
        ("CLI_", "Dòng lệnh", ""),
    ];

    private const string Header = """
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
        """;

    [Fact]
    public void TheTroubleshootingPageMatchesTheCatalog()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "troubleshooting.md");
        var expected = Generate(ErrorCatalog.For("vi"));
        if (System.Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, expected, new UTF8Encoding(false));
        }

        Assert.True(File.Exists(path), $"docs/troubleshooting.md is missing; run this test with {UpdateVariable}=1.");
        Assert.True(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) == expected,
            $"docs/troubleshooting.md differs from ErrorCatalog.vi.json; run this test with {UpdateVariable}=1.");
    }

    [Fact]
    public void EveryCodeIsInAGroup() =>
        Assert.All(ErrorCodes.All, code => Assert.Contains(Groups, g => code.StartsWith(g.Prefix, StringComparison.Ordinal)));

    /// <summary>The page: the fixed header, then each group of codes with its message (cause) and hint (fix).</summary>
    public static string Generate(ErrorCatalog catalog)
    {
        var text = new StringBuilder(Header.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        foreach (var (prefix, title, about) in Groups)
        {
            text.Append($"\n## {title}\n\n");
            if (about.Length > 0)
            {
                text.Append(about).Append("\n\n");
            }

            foreach (var code in ErrorCodes.All.Where(c => c.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var entry = catalog.Find(code)!;
                text.Append($"### `{code}`\n\n**Nguyên nhân:** {Show(entry.Message)}\n\n");
                if (entry.Hint is { Length: > 0 } hint)
                {
                    text.Append($"**Cách sửa:** {Show(hint)}\n\n");
                }
            }
        }

        return text.ToString().TrimEnd() + "\n";
    }

    // A placeholder is shown as <name>: the real value is in the message ARROW prints.
    private static string Show(string text) => Placeholder().Replace(text, "`<$1>`");

    [GeneratedRegex(@"\{([a-z_]+)\}")]
    private static partial Regex Placeholder();

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ARROW.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("ARROW.slnx not found above " + AppContext.BaseDirectory);
    }
}
