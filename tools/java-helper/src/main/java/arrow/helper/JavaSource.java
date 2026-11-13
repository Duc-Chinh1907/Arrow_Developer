package arrow.helper;

import com.github.javaparser.JavaParser;
import com.github.javaparser.ParseResult;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.Position;
import com.github.javaparser.Problem;
import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.comments.Comment;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Optional;
import java.util.Set;

/** Đọc mã Java bằng JavaParser và các tiện ích dùng chung của các lệnh. */
public final class JavaSource {

    /** Annotation đánh dấu test method của JUnit 4/5 và TestNG (so theo tên ngắn). */
    public static final Set<String> TEST_ANNOTATIONS = Set.of("Test", "ParameterizedTest", "RepeatedTest", "TestFactory", "TestTemplate");

    private JavaSource() {
    }

    /** Kết quả parse: cây cú pháp nếu được, nếu không thì thông báo lỗi đầu tiên. */
    public record Parsed(Optional<CompilationUnit> unit, String error) {
        public boolean ok() {
            return unit.isPresent();
        }

        public CompilationUnit get() {
            return unit.orElseThrow(() -> new IllegalArgumentException("Mã không parse được: " + error));
        }
    }

    /** Parse ở mức Java 21 (đủ cho mọi dự án Java 8–21), có gắn comment vào node. */
    public static Parsed parse(String source) {
        ParserConfiguration configuration = new ParserConfiguration()
                .setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_21)
                .setAttributeComments(true);
        ParseResult<CompilationUnit> result = new JavaParser(configuration).parse(source);
        if (result.isSuccessful() && result.getResult().isPresent()) {
            return new Parsed(result.getResult(), null);
        }
        String error = result.getProblems().stream().findFirst().map(Problem::getVerboseMessage).orElse("không rõ lỗi");
        return new Parsed(Optional.empty(), error.lines().findFirst().orElse(error));
    }

    public static boolean isTest(MethodDeclaration method) {
        return method.getAnnotations().stream().anyMatch(a -> TEST_ANNOTATIONS.contains(simpleName(a.getNameAsString())));
    }

    public static String simpleName(String name) {
        int dot = name.lastIndexOf('.');
        return dot >= 0 ? name.substring(dot + 1) : name;
    }

    /** Class chính của file: class trùng tên ưu tiên, rồi class public, rồi class đầu tiên. */
    public static TypeDeclaration<?> primaryType(CompilationUnit unit, String preferred) {
        if (unit.getTypes().isEmpty()) {
            throw new IllegalArgumentException("File không có class nào");
        }
        if (preferred != null) {
            for (TypeDeclaration<?> type : unit.getTypes()) {
                if (type.getNameAsString().equals(preferred)) {
                    return type;
                }
            }
        }
        return unit.getTypes().stream().filter(TypeDeclaration::isPublic).findFirst().orElse(unit.getType(0));
    }

    /**
     * Bỏ comment. Có cây cú pháp thì bỏ đúng các comment JavaParser tìm thấy; không parse được thì dùng bộ tách từ
     * (bỏ qua chuỗi, ký tự, text block) để {@code "http://..."} không bao giờ bị cắt.
     */
    public static String stripComments(String source) {
        Parsed parsed = parse(source);
        if (!parsed.ok()) {
            return stripCommentsLexically(source);
        }
        LineIndex index = new LineIndex(source);
        List<int[]> spans = new ArrayList<>();
        for (Comment comment : parsed.get().getAllComments()) {
            comment.getRange().ifPresent(r -> spans.add(new int[] {index.offset(r.begin), index.offset(r.end) + 1}));
        }
        spans.sort(Comparator.comparingInt(s -> s[0]));
        StringBuilder builder = new StringBuilder();
        int cursor = 0;
        for (int[] span : spans) {
            if (span[0] < cursor) {
                continue;
            }
            builder.append(source, cursor, span[0]).append(' ');
            cursor = Math.min(span[1], source.length());
        }
        return builder.append(source.substring(cursor)).toString();
    }

    /** Bỏ comment không cần cây cú pháp (cho mã không parse được). */
    public static String stripCommentsLexically(String source) {
        StringBuilder out = new StringBuilder(source.length());
        int i = 0;
        int n = source.length();
        while (i < n) {
            char c = source.charAt(i);
            if (c == '"' && source.startsWith("\"\"\"", i)) {
                int end = source.indexOf("\"\"\"", i + 3);
                end = end < 0 ? n : end + 3;
                out.append(source, i, end);
                i = end;
            } else if (c == '"' || c == '\'') {
                int j = i + 1;
                while (j < n && source.charAt(j) != c && source.charAt(j) != '\n') {
                    j += source.charAt(j) == '\\' ? 2 : 1;
                }
                j = Math.min(n, j + 1);
                out.append(source, i, j);
                i = j;
            } else if (c == '/' && i + 1 < n && source.charAt(i + 1) == '/') {
                int end = source.indexOf('\n', i);
                i = end < 0 ? n : end;
                out.append(' ');
            } else if (c == '/' && i + 1 < n && source.charAt(i + 1) == '*') {
                int end = source.indexOf("*/", i + 2);
                i = end < 0 ? n : end + 2;
                out.append(' ');
            } else {
                out.append(c);
                i++;
            }
        }
        return out.toString();
    }

    /** Thu mọi khoảng trắng liên tiếp ngoài chuỗi/ký tự thành một dấu cách và bỏ khoảng trắng hai đầu. */
    public static String collapseWhitespace(String source) {
        StringBuilder out = new StringBuilder(source.length());
        int i = 0;
        int n = source.length();
        boolean pendingSpace = false;
        while (i < n) {
            char c = source.charAt(i);
            if (Character.isWhitespace(c)) {
                pendingSpace = out.length() > 0;
                i++;
                continue;
            }
            if (pendingSpace) {
                out.append(' ');
                pendingSpace = false;
            }
            if (c == '"' && source.startsWith("\"\"\"", i)) {
                int end = source.indexOf("\"\"\"", i + 3);
                end = end < 0 ? n : end + 3;
                out.append(source, i, end);
                i = end;
            } else if (c == '"' || c == '\'') {
                int j = i + 1;
                while (j < n && source.charAt(j) != c && source.charAt(j) != '\n') {
                    j += source.charAt(j) == '\\' ? 2 : 1;
                }
                j = Math.min(n, j + 1);
                out.append(source, i, j);
                i = j;
            } else {
                out.append(c);
                i++;
            }
        }
        return out.toString();
    }

    /** Vị trí (dòng, cột) → chỉ số ký tự trong đúng văn bản đã parse. */
    public static final class LineIndex {
        private final String text;
        private final List<Integer> starts = new ArrayList<>();

        public LineIndex(String text) {
            this.text = text;
            starts.add(0);
            for (int i = 0; i < text.length(); i++) {
                if (text.charAt(i) == '\n') {
                    starts.add(i + 1);
                }
            }
        }

        public int offset(Position position) {
            int line = Math.max(1, Math.min(position.line, starts.size()));
            return Math.min(text.length(), starts.get(line - 1) + position.column - 1);
        }

        public String slice(Range range) {
            return text.substring(offset(range.begin), Math.min(text.length(), offset(range.end) + 1));
        }
    }

    /** Vùng của một khai báo, gồm cả Javadoc/comment gắn với nó. */
    public static Range withComment(Node node) {
        Range range = node.getRange().orElseThrow();
        Optional<Range> comment = node.getComment().flatMap(Node::getRange);
        return comment.isPresent() && comment.get().begin.isBefore(range.begin) ? new Range(comment.get().begin, range.end) : range;
    }
}
