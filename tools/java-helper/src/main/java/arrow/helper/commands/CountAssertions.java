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
 * Bổ sung ở 1.1.0 theo R03 §3.6 (15/10/2026): {@code inOrder.verify(…)}, {@code verifyNoInteractions}/{@code verifyNoMoreInteractions}/
 * {@code verifyZeroInteractions}, BDD {@code then(mock).should…()} và {@code then(actual).is…()}, MockMvc {@code andExpect(…)},
 * {@code thrown.expect…(…)} của {@code ExpectedException}.
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
        if (name.equals("verify") && arguments > 0 && (isMockitoScope(call) || isInOrderScope(call))) {
            return true;
        }
        // R03 §3.6 (15/10/2026): verifyNoInteractions, verifyNoMoreInteractions, verifyZeroInteractions.
        if (VERIFY_NO.contains(name) && arguments > 0 && isMockitoScope(call)) {
            return true;
        }
        // BDD Mockito: then(mock).should…(); counted once, at the should… call.
        if (name.startsWith("should") && call.getScope().filter(CountAssertions::isBddThen).isPresent()) {
            return true;
        }
        // BDD AssertJ: then(actual).is…(); counted once, at the then(…) call.
        if (isBddThen(call) && call.getParentNode().filter(p -> p instanceof MethodCallExpr parent
                && parent.getScope().filter(s -> s == call).isPresent()
                && parent.getNameAsString().startsWith("is")).isPresent()) {
            return true;
        }
        // MockMvc: andExpect(status().isOk()).
        if (name.equals("andExpect") && arguments > 0) {
            return true;
        }
        // JUnit 4 ExpectedException: thrown.expect(…), thrown.expectMessage(…), thrown.expectCause(…).
        if (name.startsWith("expect") && arguments > 0 && isExpectedExceptionScope(call)) {
            return true;
        }
        return isRestAssuredChain(call) && arguments > 0 && !name.equals("extract");
    }

    private static final java.util.Set<String> VERIFY_NO =
            java.util.Set.of("verifyNoInteractions", "verifyNoMoreInteractions", "verifyZeroInteractions");

    /** No scope (static import) or {@code Mockito.} / {@code BDDMockito.}. */
    private static boolean isMockitoScope(MethodCallExpr call) {
        return call.getScope().isEmpty() || call.getScope().get().toString().endsWith("Mockito");
    }

    /**
     * {@code inOrder.verify(…)} or {@code inOrder(a, b).verify(…)}. Without type resolution the variable is recognised by
     * its name, so that {@code signature.verify(bytes)} of {@code java.security} is not counted.
     */
    private static boolean isInOrderScope(MethodCallExpr call) {
        return call.getScope().map(scope -> scope instanceof MethodCallExpr inner
                ? inner.getNameAsString().equals("inOrder")
                : scope.toString().toLowerCase(java.util.Locale.ROOT).contains("inorder")).orElse(false);
    }

    /** {@code then(x)} with exactly one argument (BDD style); RestAssured's {@code then()} has none. */
    private static boolean isBddThen(Expression expression) {
        return expression instanceof MethodCallExpr then
                && then.getNameAsString().equals("then")
                && then.getArguments().size() == 1
                && (then.getScope().isEmpty() || then.getScope().get().toString().matches(".*(BDDMockito|BDDAssertions|Assertions)"));
    }

    /**
     * {@code thrown.expect…(…)}: called on a variable or field, never on a static entry point such as
     * {@code EasyMock.expect(…)} or on the zero-argument {@code expect()} of RestAssured.
     */
    private static boolean isExpectedExceptionScope(MethodCallExpr call) {
        return call.getScope().map(scope -> (scope instanceof com.github.javaparser.ast.expr.NameExpr
                        || scope instanceof com.github.javaparser.ast.expr.FieldAccessExpr)
                && !scope.toString().endsWith("EasyMock")).orElse(false);
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
