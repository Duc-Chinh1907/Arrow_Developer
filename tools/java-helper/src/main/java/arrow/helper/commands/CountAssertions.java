package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.expr.AnnotationExpr;
import com.github.javaparser.ast.expr.BooleanLiteralExpr;
import com.github.javaparser.ast.expr.Expression;
import com.github.javaparser.ast.expr.MemberValuePair;
import com.github.javaparser.ast.expr.MethodCallExpr;
import com.github.javaparser.ast.expr.NormalAnnotationExpr;
import com.github.javaparser.ast.expr.NullLiteralExpr;
import com.github.javaparser.ast.expr.ObjectCreationExpr;
import com.github.javaparser.ast.stmt.TryStmt;
import com.google.gson.JsonObject;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.TreeMap;

/**
 * {@code count_assertions}: đếm assertion qua cây cú pháp (AR Mục 5.2; CLAUDE.md hạng mục 10).
 * Tính: {@code assert*} của JUnit/TestNG, {@code assertThat} (AssertJ/Hamcrest), {@code @Test(expected = …)}, {@code fail()} trong {@code try},
 * {@code verify(…)} của Mockito, mỗi kiểm tra trong chuỗi {@code then()}/{@code expect()} của RestAssured.
 * Không tính: assertion tầm thường ({@code assertTrue(true)}, {@code assertEquals(x, x)}, {@code assertNull(null)}, {@code assertNotNull(new X())}),
 * {@code assertThat()} không tham số.
 * Vào: {@code code}. Ra: {@code parsed}, {@code total}, {@code per_method} (chỉ test method).
 */
public final class CountAssertions {

    private CountAssertions() {
    }

    public static Map<String, Object> run(JsonObject args) {
        JavaSource.Parsed parsed = JavaSource.parse(Json.required(args, "code"));
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", parsed.ok());
        if (!parsed.ok()) {
            result.put("total", null);
            result.put("per_method", Map.of());
            return result;
        }
        result.putAll(counts(parsed.get()));
        return result;
    }

    public static Map<String, Object> counts(CompilationUnit unit) {
        Map<String, Integer> perMethod = new TreeMap<>();
        int total = 0;
        for (MethodDeclaration method : unit.findAll(MethodDeclaration.class)) {
            int count = countIn(method);
            total += count;
            if (JavaSource.isTest(method)) {
                perMethod.merge(method.getNameAsString(), count, Integer::sum);
            }
        }
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("total", total);
        result.put("per_method", perMethod);
        return result;
    }

    static int countIn(MethodDeclaration method) {
        int count = 0;
        for (AnnotationExpr annotation : method.getAnnotations()) {
            if (JavaSource.TEST_ANNOTATIONS.contains(JavaSource.simpleName(annotation.getNameAsString())) && annotation instanceof NormalAnnotationExpr normal) {
                for (MemberValuePair pair : normal.getPairs()) {
                    if (pair.getNameAsString().equals("expected") || pair.getNameAsString().equals("expectedExceptions")) {
                        count++;
                    }
                }
            }
        }
        for (MethodCallExpr call : method.findAll(MethodCallExpr.class)) {
            if (isCounted(call)) {
                count++;
            }
        }
        return count;
    }

    static boolean isCounted(MethodCallExpr call) {
        String name = call.getNameAsString();
        int arguments = call.getArguments().size();
        if (name.startsWith("assert") && !isRestAssuredChain(call)) {
            return arguments > 0 && !isTrivial(call);
        }
        if (name.equals("fail") && call.findAncestor(TryStmt.class).map(t -> isInside(call, t.getTryBlock())).orElse(false)) {
            return true;
        }
        if (name.equals("verify") && arguments > 0
                && (call.getScope().isEmpty() || call.getScope().get().toString().endsWith("Mockito"))) {
            return true;
        }
        return isRestAssuredChain(call) && arguments > 0 && !name.equals("extract");
    }

    /** Lời gọi nằm sau {@code then()} hoặc {@code expect()} (không tham số) trong chuỗi của RestAssured. */
    static boolean isRestAssuredChain(MethodCallExpr call) {
        Optional<Expression> scope = call.getScope();
        while (scope.isPresent() && scope.get() instanceof MethodCallExpr parent) {
            String name = parent.getNameAsString();
            if (name.equals("extract")) {
                return false;
            }
            if ((name.equals("then") || name.equals("expect")) && parent.getArguments().isEmpty()) {
                return true;
            }
            scope = parent.getScope();
        }
        return false;
    }

    static boolean isTrivial(MethodCallExpr call) {
        List<Expression> args = call.getArguments();
        boolean oneOrWithMessage = args.size() == 1 || args.size() == 2;
        return switch (call.getNameAsString()) {
            case "assertTrue" -> oneOrWithMessage && args.stream().anyMatch(a -> isBoolean(a, true));
            case "assertFalse" -> oneOrWithMessage && args.stream().anyMatch(a -> isBoolean(a, false));
            case "assertNull" -> oneOrWithMessage && args.stream().anyMatch(a -> a instanceof NullLiteralExpr);
            case "assertNotNull" -> oneOrWithMessage && args.stream().anyMatch(a -> a instanceof ObjectCreationExpr);
            // JUnit 4: (message, expected, actual); JUnit 5: (expected, actual, message).
            case "assertEquals", "assertSame" -> args.size() == 2
                    ? args.get(0).equals(args.get(1))
                    : args.size() == 3 && (args.get(1).equals(args.get(2)) || args.get(0).equals(args.get(1)));
            default -> false;
        };
    }

    private static boolean isBoolean(Expression expression, boolean value) {
        return expression instanceof BooleanLiteralExpr literal && literal.getValue() == value;
    }

    private static boolean isInside(Node node, Node container) {
        for (Optional<Node> current = Optional.of(node); current.isPresent(); current = current.get().getParentNode()) {
            if (current.get() == container) {
                return true;
            }
        }
        return false;
    }
}
