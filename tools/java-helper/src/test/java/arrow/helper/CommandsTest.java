package arrow.helper;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

/** Mỗi lệnh được gọi qua đúng giao thức một dòng JSON vào, một dòng JSON ra (Main.handle). */
class CommandsTest {

    private static JsonObject call(String command, Map<String, Object> args) {
        Map<String, Object> request = Map.of("id", 7, "command", command, "args", args);
        JsonObject response = Json.parse(Json.write(Main.handle(Json.write(request))));
        assertEquals(7, response.get("id").getAsInt());
        assertTrue(response.get("ok").getAsBoolean(), () -> "lỗi: " + response.get("error"));
        return response.getAsJsonObject("result");
    }

    private static List<String> strings(JsonArray array) {
        return array.asList().stream().map(e -> e.getAsString()).toList();
    }

    private static final String CALCULATOR_TEST = """
            package com.acme;

            import static org.junit.Assert.*;
            import org.junit.Test;

            /** Test của Calculator. */
            public class CalculatorTest {
                private final Calculator calc = new Calculator();

                @Test
                public void adds() {
                    // cộng hai số
                    assertEquals(5, calc.add(2, 3));
                    assertTrue(true);
                }

                @Test(expected = ArithmeticException.class)
                public void dividesByZero() {
                    calc.divide(1, 0);
                }

                @Test
                public void failsInTry() {
                    try {
                        calc.divide(1, 0);
                        fail("expected exception");
                    } catch (ArithmeticException e) {
                        assertNotNull(e.getMessage());
                    }
                }

                private int helper() {
                    return CalculatorTest.class.getName().length();
                }
            }
            """;

    @Test
    void protocolReportsErrorsAsJson() {
        Map<String, Object> bad = Main.handle("{not json");
        assertFalse((Boolean) bad.get("ok"));
        Map<String, Object> unknown = Main.handle("{\"id\":1,\"command\":\"nope\",\"args\":{}}");
        assertFalse((Boolean) unknown.get("ok"));
        assertTrue(unknown.get("error").toString().contains("nope"));
        Map<String, Object> missing = Main.handle("{\"id\":2,\"command\":\"parse\",\"args\":{}}");
        assertTrue(missing.get("error").toString().contains("code"));
        assertEquals(Boolean.TRUE, Main.handle("{\"id\":3,\"command\":\"shutdown\"}").get("shutdown"));
    }

    @Test
    void parseReportsTypesPackageAndErrors() {
        JsonObject ok = call("parse", Map.of("code", CALCULATOR_TEST));
        assertTrue(ok.get("parsed").getAsBoolean());
        assertEquals("com.acme", ok.get("package").getAsString());
        assertEquals("CalculatorTest", ok.getAsJsonArray("top_level_types").get(0).getAsJsonObject().get("name").getAsString());
        assertEquals(3, ok.get("test_method_count").getAsInt());

        JsonObject two = call("parse", Map.of("code", "class A {} class B {}"));
        assertEquals(2, two.getAsJsonArray("top_level_types").size());

        JsonObject broken = call("parse", Map.of("code", "public class A { void x( }"));
        assertFalse(broken.get("parsed").getAsBoolean());
        assertFalse(broken.get("error").getAsString().isBlank());
    }

    @Test
    void renameClassUpdatesReferencesAndPackageButKeepsFormatting() {
        String code = """
                import org.junit.Test;

                // ghi chú giữ nguyên
                public class CalculatorTest {
                    private CalculatorTest other;

                    public CalculatorTest() {
                    }

                    @Test
                    public void   oddlyFormatted() {   int x = CalculatorTest.count(); }

                    static int count() { return new CalculatorTest().hashCode(); }
                }
                """;
        JsonObject result = call("rename_class", Map.of("code", code, "new_name", "Calculator_ArrowTest", "package", "com.acme"));
        String renamed = result.get("code").getAsString();
        assertEquals("CalculatorTest", result.get("old_name").getAsString());
        assertTrue(renamed.startsWith("package com.acme;"), renamed);
        assertFalse(renamed.contains("CalculatorTest"), renamed);
        assertTrue(renamed.contains("public class Calculator_ArrowTest {"));
        assertTrue(renamed.contains("private Calculator_ArrowTest other;"));
        assertTrue(renamed.contains("public Calculator_ArrowTest() {"));
        assertTrue(renamed.contains("new Calculator_ArrowTest().hashCode()"));
        assertTrue(renamed.contains("public void   oddlyFormatted() {   int x = Calculator_ArrowTest.count(); }"), renamed);
        assertTrue(renamed.contains("// ghi chú giữ nguyên"));
        assertEquals(List.of("package: added com.acme", "class: CalculatorTest → Calculator_ArrowTest"), strings(result.getAsJsonArray("actions")));

        JsonObject unchanged = call("rename_class", Map.of("code", code, "new_name", "CalculatorTest"));
        assertEquals(code, unchanged.get("code").getAsString());
        assertEquals(0, unchanged.getAsJsonArray("actions").size());
    }

    @Test
    void hashIgnoresCommentsAndWhitespaceButNotStrings() {
        String a = "class A { String u = \"http://x//y\"; /* c */ int f() { return 1; } }";
        String b = "class A {\n  // other comment\n  String u = \"http://x//y\";\n\n  int f() {\n    return 1;\n  }\n}\n";
        String c = "class A { String u = \"http://x//z\"; int f() { return 1; } }";
        JsonObject ra = call("strip_comments_hash", Map.of("code", a));
        JsonObject rb = call("strip_comments_hash", Map.of("code", b));
        assertEquals(ra.get("code_hash").getAsString(), rb.get("code_hash").getAsString());
        assertTrue(ra.get("normalized").getAsString().contains("\"http://x//y\""));
        assertTrue(ra.get("code_hash").getAsString().startsWith("sha256:"));
        assertNotEquals(ra.get("code_hash").getAsString(), call("strip_comments_hash", Map.of("code", c)).get("code_hash").getAsString());

        JsonObject broken = call("strip_comments_hash", Map.of("code", "class A { void x( /* c */ String s = \"//keep\" }"));
        assertFalse(broken.get("parsed").getAsBoolean());
        assertTrue(broken.get("normalized").getAsString().contains("\"//keep\""));
        assertFalse(broken.get("normalized").getAsString().contains("/* c */"));
    }

    @Test
    void countsAssertionsByRules() {
        JsonObject result = call("count_assertions", Map.of("code", CALCULATOR_TEST));
        JsonObject perMethod = result.getAsJsonObject("per_method");
        // adds: assertEquals đếm, assertTrue(true) tầm thường không đếm.
        assertEquals(1, perMethod.get("adds").getAsInt());
        // dividesByZero: @Test(expected = …).
        assertEquals(1, perMethod.get("dividesByZero").getAsInt());
        // failsInTry: fail() trong try + assertNotNull(e.getMessage()).
        assertEquals(2, perMethod.get("failsInTry").getAsInt());
        assertEquals(4, result.get("total").getAsInt());

        String more = """
                class T {
                    @org.junit.jupiter.api.Test void a() {
                        assertThat(list).hasSize(2);
                        verify(mock).save(any());
                        Mockito.verify(mock).close();
                        given().when().get("/x").then().statusCode(200).body(is("hi")).assertThat();
                        assertEquals(x, x);
                        assertNotNull(new Object());
                        assertNull(null);
                    }
                    @org.testng.annotations.Test(expectedExceptions = IllegalStateException.class) void b() { run(); }
                }
                """;
        JsonObject counted = call("count_assertions", Map.of("code", more)).getAsJsonObject("per_method");
        // assertThat(list), verify(mock), Mockito.verify(mock), statusCode(200), body(is("hi")) = 5; còn lại tầm thường hoặc không tham số.
        assertEquals(5, counted.get("a").getAsInt());
        assertEquals(1, counted.get("b").getAsInt());
    }

    @Test
    void listsTestMethodsOfAllFrameworks() {
        String code = """
                class T {
                    @Test void a() {}
                    @org.junit.jupiter.params.ParameterizedTest void b(int x) {}
                    @org.testng.annotations.Test public void c() {}
                    void helper() {}
                }
                """;
        assertEquals(List.of("a", "b", "c"), strings(call("list_test_methods", Map.of("code", code)).getAsJsonArray("methods")));
    }

    @Test
    void findsSmallestMethodContainingLine() {
        JsonObject result = call("method_at_line", Map.of("code", CALCULATOR_TEST, "lines", List.of(14, 20, 1, 33)));
        JsonObject methods = result.getAsJsonObject("methods");
        assertEquals("adds", methods.get("14").getAsString());
        assertEquals("dividesByZero", methods.get("20").getAsString());
        assertTrue(methods.get("1").isJsonNull());
        assertEquals("helper", methods.get("33").getAsString());
    }

    @Test
    void spliceReplacesDropsAndAddsWithoutTouchingOtherMethods() {
        String base = """
                package com.acme;

                import org.junit.Test;

                public class OrderService_ArrowTest {
                    @Test
                    public void keepsThis()   {  assertEquals(1, 1 + 0); }

                    @Test
                    public void totalPriceAddsTax() {
                        long total = service.getTotal(1L);
                    }

                    @Test
                    public void broken() {
                        nothing();
                    }
                }
                """;
        String response = """
                package com.acme;

                import org.junit.Test;
                import java.util.List;

                public class OrderService_ArrowTest {
                    private static final long ID = 1L;

                    @Test
                    public void keepsThis() { /* model rewrote this too, must be ignored */ }

                    @Test
                    public void totalPriceAddsTax() {
                        long total = service.totalPrice(ID);
                        assertEquals(110, total);
                    }

                    private long id() { return ID; }
                }
                """;
        JsonObject result = call("splice_methods", Map.of("base", base, "response", response, "methods", List.of("totalPriceAddsTax", "broken")));
        String code = result.get("code").getAsString();
        assertEquals(List.of("totalPriceAddsTax"), strings(result.getAsJsonArray("replaced")));
        assertEquals(List.of("broken"), strings(result.getAsJsonArray("dropped")));
        assertEquals(List.of("import java.util.List;"), strings(result.getAsJsonArray("added_imports")));
        assertEquals(List.of("ID"), strings(result.getAsJsonArray("added_fields")));
        assertEquals(List.of("id"), strings(result.getAsJsonArray("added_methods")));
        assertTrue(code.contains("public void keepsThis()   {  assertEquals(1, 1 + 0); }"), code);
        assertTrue(code.contains("long total = service.totalPrice(ID);"));
        assertFalse(code.contains("getTotal"));
        assertFalse(code.contains("broken"));
        assertFalse(code.contains("must be ignored"));
        assertTrue(call("parse", Map.of("code", code)).get("parsed").getAsBoolean(), code);
    }

    @Test
    void classApiFromSourceAndClasspath() {
        String source = """
                package com.acme;
                public class OrderService {
                    public static final int MAX = 3;
                    private int hidden;
                    public OrderService(OrderRepository repo, TaxPolicy tax) {}
                    private OrderService() {}
                    public long totalPrice(long orderId) throws IllegalStateException { return 0; }
                    void cancel(long orderId) {}
                    private void secret() {}
                }
                """;
        JsonObject result = call("class_api", Map.of("sources", List.of(source), "classes", List.of("java.util.Optional", "com.missing.Nope"), "classpath", List.of()));
        JsonObject order = result.getAsJsonArray("types").get(0).getAsJsonObject();
        assertEquals("com.acme.OrderService", order.get("name").getAsString());
        assertEquals("source", order.get("origin").getAsString());
        List<String> members = strings(order.getAsJsonArray("members"));
        assertTrue(members.contains("public static final int MAX"), members.toString());
        assertTrue(members.contains("public OrderService(OrderRepository repo, TaxPolicy tax)"), members.toString());
        assertTrue(members.contains("public long totalPrice(long orderId) throws IllegalStateException"), members.toString());
        assertTrue(members.contains("void cancel(long orderId)"), members.toString());
        assertFalse(members.stream().anyMatch(m -> m.contains("secret") || m.contains("hidden") || m.equals("private OrderService()")));

        JsonObject optional = result.getAsJsonArray("types").get(1).getAsJsonObject();
        assertEquals("java.util.Optional", optional.get("name").getAsString());
        assertEquals("classpath", optional.get("origin").getAsString());
        assertTrue(strings(optional.getAsJsonArray("members")).stream().anyMatch(m -> m.startsWith("public static") && m.contains(" of(T)")));
        assertEquals("com.missing.Nope", result.getAsJsonArray("missing").get(0).getAsJsonObject().get("name").getAsString());
    }

    @Test
    void methodSourceAndCalledMethods() {
        String cut = """
                public class OrderService {
                    private final TaxPolicy tax;
                    public OrderService(TaxPolicy tax) { this.tax = tax; }
                    public long totalPrice(long id) { return tax.apply(100); }
                    public long totalPrice(long id, boolean rounded) { return 0; }
                    public void cancel(long id) {}
                }
                """;
        JsonObject sources = call("method_source", Map.of("code", cut, "names", List.of("totalPrice", "<init>")));
        assertEquals(3, sources.getAsJsonArray("methods").size());
        assertTrue(sources.getAsJsonArray("methods").get(0).getAsJsonObject().get("source").getAsString().startsWith("public OrderService(TaxPolicy tax)"));

        String test = """
                class T {
                    @Test void a() { service.totalPrice(1L); }
                    @Test void b() { service.cancel(1L); }
                }
                """;
        JsonObject called = call("called_methods", Map.of("test_code", test, "test_methods", List.of("a"), "cut_code", cut));
        assertEquals(List.of("totalPrice"), strings(called.getAsJsonArray("called")));
        assertEquals(2, called.getAsJsonArray("methods").size());
        assertEquals(1, called.getAsJsonArray("constructors").size());
        assertEquals("private final TaxPolicy tax;", called.getAsJsonArray("fields").get(0).getAsString());
        assertNull(null);
    }

    @Test
    void shrinkSourceRemovesCommentsThenBodies() {
        String cut = """
                package com.acme;
                /** Một class dài. */
                public class Big {
                    // comment rất dài ......................................................................
                    public int first(int a) {
                        return a + 1;
                    }

                    public int second(int b) {
                        int x = b * 2;
                        int y = x * 3;
                        return x + y;
                    }
                }
                """;
        JsonObject none = call("shrink_source", Map.of("code", cut, "max_chars", 10_000));
        assertEquals("none", none.get("truncation").getAsString());
        assertEquals(cut, none.get("code").getAsString());

        JsonObject stripped = call("shrink_source", Map.of("code", cut, "max_chars", 230));
        assertEquals("comments_removed", stripped.get("truncation").getAsString());
        String noComments = stripped.get("code").getAsString();
        assertFalse(noComments.contains("comment") || noComments.contains("/**"), noComments);
        assertTrue(noComments.contains("return x + y;"));

        JsonObject signatures = call("shrink_source", Map.of("code", cut, "max_chars", 190));
        assertEquals("signatures_only", signatures.get("truncation").getAsString());
        String text = signatures.get("code").getAsString();
        assertTrue(text.length() <= 190, text);
        assertTrue(text.contains("return a + 1;"), text);
        assertTrue(text.contains("public int second(int b) { /* body omitted */ }"), text);
        assertTrue(call("parse", Map.of("code", text)).get("parsed").getAsBoolean(), text);
    }
}
