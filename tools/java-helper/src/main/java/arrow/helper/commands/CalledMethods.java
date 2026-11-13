package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import arrow.helper.JavaSource.LineIndex;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.github.javaparser.ast.body.FieldDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.expr.MethodCallExpr;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * {@code called_methods}: các method của CUT mà các test method (đã cho) gọi tới, kèm mã nguồn của chúng, constructor và
 * khai báo field của CUT. Dùng cho thông tin bổ sung mức 2 ASSERTION và RUNTIME (spec 03 Mục 6.5).
 * Vào: {@code test_code}, {@code test_methods} (rỗng = mọi method), {@code cut_code}.
 * Ra: {@code called} (tên), {@code methods} ({@code name}, {@code source}), {@code constructors}, {@code fields}.
 */
public final class CalledMethods {

    private CalledMethods() {
    }

    public static Map<String, Object> run(JsonObject args) {
        String testCode = Json.required(args, "test_code");
        String cutCode = Json.required(args, "cut_code");
        Set<String> wanted = new HashSet<>(Json.strings(args, "test_methods"));
        JavaSource.Parsed test = JavaSource.parse(testCode);
        JavaSource.Parsed cut = JavaSource.parse(cutCode);
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", test.ok() && cut.ok());
        if (!test.ok() || !cut.ok()) {
            result.put("called", List.of());
            result.put("methods", List.of());
            result.put("constructors", List.of());
            result.put("fields", List.of());
            return result;
        }

        CompilationUnit cutUnit = cut.get();
        Set<String> cutMethods = new LinkedHashSet<>();
        cutUnit.findAll(MethodDeclaration.class).forEach(m -> cutMethods.add(m.getNameAsString()));
        Set<String> called = new LinkedHashSet<>();
        for (MethodDeclaration method : test.get().findAll(MethodDeclaration.class)) {
            if (wanted.isEmpty() || wanted.contains(method.getNameAsString())) {
                for (MethodCallExpr call : method.findAll(MethodCallExpr.class)) {
                    if (cutMethods.contains(call.getNameAsString())) {
                        called.add(call.getNameAsString());
                    }
                }
            }
        }

        LineIndex index = new LineIndex(cutCode);
        List<String> constructors = new ArrayList<>();
        cutUnit.findAll(ConstructorDeclaration.class).forEach(c -> c.getRange().ifPresent(r -> constructors.add(index.slice(r))));
        List<String> fields = new ArrayList<>();
        cutUnit.findAll(FieldDeclaration.class).forEach(f -> f.getRange().ifPresent(r -> fields.add(index.slice(r))));

        result.put("called", new ArrayList<>(called));
        result.put("methods", MethodSource.sources(cutCode, cutUnit, called));
        result.put("constructors", constructors);
        result.put("fields", fields);
        return result;
    }
}
