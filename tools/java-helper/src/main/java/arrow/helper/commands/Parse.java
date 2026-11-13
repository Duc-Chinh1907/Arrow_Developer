package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * {@code parse}: kiểm tra cú pháp mã model trả về (spec 02 Mục 4.5 bước 2).
 * Vào: {@code code}. Ra: {@code parsed}, {@code error}, {@code package}, {@code top_level_types} (tên, public), {@code test_method_count}.
 */
public final class Parse {

    private Parse() {
    }

    public static Map<String, Object> run(JsonObject args) {
        JavaSource.Parsed parsed = JavaSource.parse(Json.required(args, "code"));
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", parsed.ok());
        result.put("error", parsed.error());
        List<Map<String, Object>> types = new ArrayList<>();
        String pkg = null;
        int tests = 0;
        if (parsed.ok()) {
            CompilationUnit unit = parsed.get();
            pkg = unit.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
            for (TypeDeclaration<?> type : unit.getTypes()) {
                Map<String, Object> entry = new LinkedHashMap<>();
                entry.put("name", type.getNameAsString());
                entry.put("public", type.isPublic());
                types.add(entry);
            }
            tests = ListTestMethods.testMethods(unit).size();
        }
        result.put("package", pkg);
        result.put("top_level_types", types);
        result.put("test_method_count", tests);
        return result;
    }
}
