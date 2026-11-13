package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import arrow.helper.JavaSource.LineIndex;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.CallableDeclaration;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * {@code method_source}: mã nguồn nguyên văn của các method theo tên (mọi overload); {@code <init>} là constructor.
 * Dùng cho thông tin bổ sung mức 2 RUNTIME (method trong stack trace). Vào: {@code code}, {@code names}. Ra: {@code methods} ({@code name}, {@code source}).
 */
public final class MethodSource {

    private MethodSource() {
    }

    public static Map<String, Object> run(JsonObject args) {
        String code = Json.required(args, "code");
        JavaSource.Parsed parsed = JavaSource.parse(code);
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", parsed.ok());
        result.put("methods", parsed.ok() ? sources(code, parsed.get(), new LinkedHashSet<>(Json.strings(args, "names"))) : List.of());
        return result;
    }

    static List<Map<String, Object>> sources(String code, CompilationUnit unit, Set<String> names) {
        LineIndex index = new LineIndex(code);
        List<Map<String, Object>> methods = new ArrayList<>();
        for (CallableDeclaration<?> callable : unit.findAll(CallableDeclaration.class)) {
            boolean constructor = callable instanceof ConstructorDeclaration;
            String name = constructor ? "<init>" : callable.getNameAsString();
            if (names.contains(name) && callable.getRange().isPresent()) {
                Map<String, Object> entry = new LinkedHashMap<>();
                entry.put("name", name);
                entry.put("source", index.slice(callable.getRange().get()));
                methods.add(entry);
            }
        }
        return methods;
    }
}
