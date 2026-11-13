package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.CallableDeclaration;
import com.google.gson.JsonObject;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Optional;

/**
 * {@code method_at_line}: method (hoặc constructor) nhỏ nhất chứa mỗi dòng; dùng để biết method nào chứa lỗi biên dịch
 * (COMPILE mức 3, spec 03 Mục 5). Vào: {@code code}, {@code lines}. Ra: {@code parsed}, {@code methods} (dòng → tên, hoặc null).
 */
public final class MethodAtLine {

    private MethodAtLine() {
    }

    public static Map<String, Object> run(JsonObject args) {
        JavaSource.Parsed parsed = JavaSource.parse(Json.required(args, "code"));
        Map<String, Object> methods = new LinkedHashMap<>();
        for (int line : Json.ints(args, "lines")) {
            methods.put(String.valueOf(line), parsed.ok() ? methodAt(parsed.get(), line) : null);
        }
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", parsed.ok());
        result.put("methods", methods);
        return result;
    }

    static String methodAt(CompilationUnit unit, int line) {
        String name = null;
        int best = Integer.MAX_VALUE;
        for (CallableDeclaration<?> callable : unit.findAll(CallableDeclaration.class)) {
            Optional<Range> range = callable.getRange();
            if (range.isPresent() && range.get().begin.line <= line && line <= range.get().end.line) {
                int size = range.get().end.line - range.get().begin.line;
                if (size < best) {
                    best = size;
                    name = callable.getNameAsString();
                }
            }
        }
        return name;
    }
}
