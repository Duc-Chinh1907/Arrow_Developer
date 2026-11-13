package arrow.helper;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.jar.JarEntry;
import java.util.jar.JarOutputStream;
import javax.tools.JavaCompiler;
import javax.tools.ToolProvider;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** What java-helper 1.1.0 adds for the product (P10 §4.8, §4.10, §4.13, §7). */
class ProductCommandsTest {

    private static JsonObject call(String command, Map<String, Object> args) {
        Map<String, Object> request = Map.of("id", 9, "command", command, "args", args);
        JsonObject response = Json.parse(Json.write(Main.handle(Json.write(request))));
        assertTrue(response.get("ok").getAsBoolean(), () -> "error: " + response.get("error"));
        return response.getAsJsonObject("result");
    }

    /** Assertions counted in the single test method {@code t} of a class built around {@code body}. */
    private static int count(String body) {
        String code = """
                import org.junit.Test;
                class SomeTest {
                    @Test public void t() throws Exception {
                %s
                    }
                }
                """.formatted(body);
        JsonObject result = call("count_assertions", Map.of("code", code));
        assertTrue(result.get("parsed").getAsBoolean(), code);
        return result.getAsJsonObject("per_method").get("t").getAsInt();
    }

    @Test
    void pingReportsVersion110() {
        assertEquals("1.1.0", call("ping", Map.of()).get("version").getAsString());
    }

    // ── count_assertions: kinds added by R03 §3.6 on 15/10/2026, one test each ──────────────

    @Test
    void countsInOrderVerify() {
        assertEquals(2, count("InOrder inOrder = inOrder(a, b); inOrder.verify(a).first(); inOrder.verify(b).second();"));
        assertEquals(1, count("Mockito.inOrder(a).verify(a).run();"));
    }

    @Test
    void countsVerifyNoInteractionsFamily() {
        assertEquals(3, count("verifyNoInteractions(a); verifyNoMoreInteractions(b); Mockito.verifyZeroInteractions(c);"));
    }

    @Test
    void countsBddMockitoThenShould() {
        assertEquals(3, count("then(repo).should().save(order); BDDMockito.then(repo).should(times(2)).find(1L); then(repo).shouldHaveNoMoreInteractions();"));
    }

    @Test
    void countsBddAssertJThenIs() {
        assertEquals(2, count("then(total).isEqualTo(10); BDDAssertions.then(list).isNotEmpty();"));
    }

    @Test
    void countsMockMvcAndExpect() {
        assertEquals(2, count("mvc.perform(get(\"/orders\")).andExpect(status().isOk()).andExpect(jsonPath(\"$.size()\").value(2));"));
    }

    @Test
    void countsExpectedExceptionRule() {
        assertEquals(3, count("thrown.expect(IllegalArgumentException.class); this.thrown.expectMessage(\"bad\"); thrown.expectCause(isA(IOException.class)); service.run();"));
    }

    @Test
    void restAssuredThenIsNotTakenForBdd() {
        // then() without argument is RestAssured: each check after it counts, the then() itself does not.
        assertEquals(2, count("given().when().get(\"/x\").then().statusCode(200).body(\"id\", equalTo(1));"));
    }

    @Test
    void lookAlikesAreNotCounted() {
        // java.security.Signature.verify, EasyMock.expect, Mockito's when(…).then(answer), RestAssured expect().
        assertEquals(0, count("signature.verify(bytes); EasyMock.expect(mock.size()).andReturn(1); when(mock.size()).then(answer); given().expect();"));
        // Existing rules stay: trivial assertions are still not counted.
        assertEquals(0, count("assertTrue(true); assertEquals(x, x);"));
    }

    // ── class_api: reading a jar never initialises the class ───────────────────────────────

    @Test
    void classApiReadsAClassWhoseStaticInitializerThrows(@TempDir Path temp) throws IOException {
        Path src = temp.resolve("src/lib/Exploding.java");
        Files.createDirectories(src.getParent());
        Files.writeString(src, """
                package lib;
                public class Exploding {
                    static { if (true) throw new IllegalStateException("must not run"); }
                    public static int answer() { return 42; }
                    public Exploding(String name) {}
                }
                """);
        Path classes = temp.resolve("classes");
        Files.createDirectories(classes);
        JavaCompiler javac = ToolProvider.getSystemJavaCompiler();
        assertNotNull(javac, "tests need a JDK, not a JRE");
        assertEquals(0, javac.run(null, null, null, "-d", classes.toString(), src.toString()));
        Path jar = temp.resolve("exploding.jar");
        try (JarOutputStream out = new JarOutputStream(Files.newOutputStream(jar))) {
            out.putNextEntry(new JarEntry("lib/Exploding.class"));
            out.write(Files.readAllBytes(classes.resolve("lib/Exploding.class")));
            out.closeEntry();
        }

        JsonObject result = call("class_api", Map.of("sources", List.of(), "classes", List.of("lib.Exploding"), "classpath", List.of(jar.toString())));

        JsonObject type = result.getAsJsonArray("types").get(0).getAsJsonObject();
        assertEquals("classpath", type.get("origin").getAsString());
        String members = type.getAsJsonArray("members").toString();
        assertTrue(members.contains("public static int answer()"), members);
        assertTrue(members.contains("public Exploding(String)"), members);
        assertEquals(0, result.getAsJsonArray("missing").size());
    }

    // ── list_classes ────────────────────────────────────────────────────────────────────

    @Test
    void listClassesReadsTopLevelTypesOfEveryRoot(@TempDir Path temp) throws IOException {
        Path main = temp.resolve("core/src/main/java");
        Path other = temp.resolve("api/src/main/java");
        write(main, "com/acme/shop/OrderService.java", "package com.acme.shop; public class OrderService { static class Inner {} }");
        write(main, "com/acme/shop/Base.java", "package com.acme.shop; abstract class Base {} enum Status { NEW }");
        write(main, "com/acme/shop/Repo.java", "package com.acme.shop; public interface Repo {}");
        write(main, "com/acme/shop/package-info.java", "package com.acme.shop;");
        write(main, "module-info.java", "module shop {}");
        write(main, "com/acme/shop/Broken.java", "package com.acme.shop; public class Broken { void x( }");
        write(other, "com/acme/api/Money.java", "package com.acme.api; public record Money(long cents) {}");
        write(other, "com/acme/api/Audited.java", "package com.acme.api; public @interface Audited {}");
        write(other, "Plain.java", "class Plain {}");

        JsonObject result = call("list_classes", Map.of("roots", List.of(main.toString(), other.toString())));

        Map<String, JsonObject> byFqn = new java.util.TreeMap<>();
        for (JsonElement e : result.getAsJsonArray("classes")) {
            byFqn.put(e.getAsJsonObject().get("fqn").getAsString(), e.getAsJsonObject());
        }
        assertEquals(List.of("Plain", "com.acme.api.Audited", "com.acme.api.Money", "com.acme.shop.Base", "com.acme.shop.OrderService",
                "com.acme.shop.Repo", "com.acme.shop.Status"), List.copyOf(byFqn.keySet()));
        assertEquals("class", byFqn.get("com.acme.shop.OrderService").get("kind").getAsString());
        assertTrue(byFqn.get("com.acme.shop.OrderService").get("is_public").getAsBoolean());
        assertTrue(byFqn.get("com.acme.shop.Base").get("is_abstract").getAsBoolean());
        assertFalse(byFqn.get("com.acme.shop.Base").get("is_public").getAsBoolean());
        assertEquals("enum", byFqn.get("com.acme.shop.Status").get("kind").getAsString());
        assertEquals("interface", byFqn.get("com.acme.shop.Repo").get("kind").getAsString());
        assertEquals("record", byFqn.get("com.acme.api.Money").get("kind").getAsString());
        assertEquals("annotation", byFqn.get("com.acme.api.Audited").get("kind").getAsString());
        assertEquals("OrderService", byFqn.get("com.acme.shop.OrderService").get("simple_name").getAsString());
        assertEquals(other.toAbsolutePath().normalize().toString(), byFqn.get("com.acme.api.Money").get("root").getAsString());
        assertTrue(byFqn.get("com.acme.api.Money").get("path").getAsString().endsWith("Money.java"));

        JsonArray errors = result.getAsJsonArray("errors");
        assertEquals(1, errors.size());
        assertTrue(errors.get(0).getAsJsonObject().get("path").getAsString().endsWith("Broken.java"));
        assertFalse(result.get("truncated").getAsBoolean());
    }

    @Test
    void listClassesStopsAtMaxFiles(@TempDir Path temp) throws IOException {
        for (int i = 0; i < 5; i++) {
            write(temp, "p/C" + i + ".java", "package p; class C" + i + " {}");
        }
        JsonObject result = call("list_classes", Map.of("roots", List.of(temp.toString()), "max_files", 3));
        assertEquals(3, result.getAsJsonArray("classes").size());
        assertTrue(result.get("truncated").getAsBoolean());
    }

    @Test
    void listClassesReportsAMissingRootWithoutFailing(@TempDir Path temp) {
        JsonObject result = call("list_classes", Map.of("roots", List.of(temp.resolve("nope").toString())));
        assertEquals(0, result.getAsJsonArray("classes").size());
        assertEquals(1, result.getAsJsonArray("errors").size());
    }

    // ── Java 8–21 syntax (P10 §2, §7) ─────────────────────────────────────────────────────

    @Test
    void parsesModernJavaSyntax() {
        String code = """
                package com.acme;
                public sealed interface Shape permits Circle, Square {}
                record Circle(double r) implements Shape {}
                final class Square implements Shape {
                    String describe(Object o) {
                        String text = \"""
                            multi-line
                            \""";
                        return switch (o) {
                            case Circle c when c.r() > 1 -> "big circle";
                            case Circle c -> "circle";
                            default -> o instanceof String s ? s : text;
                        };
                    }
                }
                """;
        JsonObject result = call("parse", Map.of("code", code));
        assertTrue(result.get("parsed").getAsBoolean(), () -> String.valueOf(result.get("error")));
        assertEquals(3, result.getAsJsonArray("top_level_types").size());
    }

    private static void write(Path root, String relative, String content) throws IOException {
        Path file = root.resolve(relative);
        Files.createDirectories(file.getParent());
        try (OutputStream out = Files.newOutputStream(file)) {
            out.write(content.getBytes(StandardCharsets.UTF_8));
        }
    }
}
