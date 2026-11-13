package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.google.gson.JsonObject;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * {@code list_test_methods}: tên các test method (có {@code @Test}, {@code @ParameterizedTest}, {@code @RepeatedTest},
 * {@code @TestFactory}, {@code @TestTemplate}; JUnit 4/5 và TestNG). Dùng để đếm method bị bỏ ở mức 3 và mật độ test smell.
 * Vào: {@code code}. Ra: {@code parsed}, {@code methods}.
 */
public final class ListTestMethods {

    private ListTestMethods() {
    }

    public static Map<String, Object> run(JsonObject args) {
        JavaSource.Parsed parsed = JavaSource.parse(Json.required(args, "code"));
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", parsed.ok());
        result.put("methods", parsed.ok() ? testMethods(parsed.get()) : List.of());
        return result;
    }

    public static List<String> testMethods(CompilationUnit unit) {
        return unit.findAll(MethodDeclaration.class).stream()
                .filter(JavaSource::isTest)
                .map(MethodDeclaration::getNameAsString)
                .distinct()
                .toList();
    }
}
