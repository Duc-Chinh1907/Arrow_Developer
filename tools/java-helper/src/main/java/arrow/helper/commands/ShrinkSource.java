package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import arrow.helper.JavaSource.LineIndex;
import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.CallableDeclaration;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.stmt.BlockStmt;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;

/**
 * {@code shrink_source}: rút gọn mã CUT quá dài trước khi đưa vào prompt (spec 02 Mục 2.1 bước 2).
 * Dài hơn {@code max_chars}: bỏ comment và dòng trống ({@code comments_removed}); vẫn dài hơn: giữ mọi khai báo và chữ ký
 * constructor/method, thân method giữ lần lượt theo thứ tự trong file tới khi đủ giới hạn, thân còn lại thay bằng
 * {@code { ... }} ({@code signatures_only}). Vào: {@code code}, {@code max_chars}. Ra: {@code code}, {@code truncation}.
 */
public final class ShrinkSource {

    static final String OMITTED = "{ /* body omitted */ }";

    private ShrinkSource() {
    }

    public static Map<String, Object> run(JsonObject args) {
        String code = Json.required(args, "code").replace("\r\n", "\n");
        int max = args.has("max_chars") ? args.get("max_chars").getAsInt() : 48_000;
        Map<String, Object> result = new LinkedHashMap<>();
        if (code.length() <= max) {
            result.put("code", code);
            result.put("truncation", "none");
            return result;
        }

        String stripped = dropBlankLines(JavaSource.stripComments(code));
        if (stripped.length() <= max) {
            result.put("code", stripped);
            result.put("truncation", "comments_removed");
            return result;
        }

        result.put("code", signaturesOnly(stripped, max));
        result.put("truncation", "signatures_only");
        return result;
    }

    /** Bỏ dòng chỉ có khoảng trắng và khoảng trắng cuối dòng (comment đã bỏ để lại dòng trống). */
    static String dropBlankLines(String code) {
        StringBuilder out = new StringBuilder(code.length());
        for (String line : code.split("\n", -1)) {
            String trimmed = line.stripTrailing();
            if (!trimmed.isEmpty()) {
                out.append(trimmed).append('\n');
            }
        }
        return out.toString();
    }

    private record Body(int start, int end) {
        int saving() {
            return (end - start) - OMITTED.length();
        }
    }

    static String signaturesOnly(String code, int max) {
        JavaSource.Parsed parsed = JavaSource.parse(code);
        if (!parsed.ok()) {
            return code.substring(0, Math.min(code.length(), max));
        }
        CompilationUnit unit = parsed.get();
        LineIndex index = new LineIndex(code);
        List<Body> bodies = new ArrayList<>();
        for (CallableDeclaration<?> callable : unit.findAll(CallableDeclaration.class)) {
            Optional<BlockStmt> body = callable instanceof MethodDeclaration method ? method.getBody()
                    : callable instanceof ConstructorDeclaration constructor ? Optional.of(constructor.getBody()) : Optional.empty();
            if (body.isEmpty() || body.get().getRange().isEmpty()) {
                continue;
            }
            Range range = body.get().getRange().get();
            bodies.add(new Body(index.offset(range.begin), index.offset(range.end) + 1));
        }
        // Chỉ xét thân ngoài cùng: method của class ẩn danh/cục bộ nằm trong một thân khác thì đi theo thân đó.
        bodies.sort(Comparator.comparingInt(Body::start));
        List<Body> outer = new ArrayList<>();
        int lastEnd = -1;
        for (Body body : bodies) {
            if (body.start() >= lastEnd) {
                outer.add(body);
                lastEnd = body.end();
            }
        }

        // Mọi thân bị thay; rồi giữ lại lần lượt từ đầu file khi còn đủ chỗ.
        int length = code.length();
        for (Body body : outer) {
            length -= body.saving();
        }
        boolean[] keep = new boolean[outer.size()];
        for (int i = 0; i < outer.size(); i++) {
            if (length + outer.get(i).saving() <= max) {
                keep[i] = true;
                length += outer.get(i).saving();
            }
        }

        StringBuilder out = new StringBuilder();
        int cursor = 0;
        for (int i = 0; i < outer.size(); i++) {
            Body body = outer.get(i);
            out.append(code, cursor, body.start());
            out.append(keep[i] ? code.substring(body.start(), body.end()) : OMITTED);
            cursor = body.end();
        }
        out.append(code.substring(cursor));
        String text = out.toString();
        return text.length() <= max ? text : text.substring(0, max);
    }
}
