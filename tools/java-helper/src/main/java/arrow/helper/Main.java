package arrow.helper;

import arrow.helper.commands.CalledMethods;
import arrow.helper.commands.ClassApi;
import arrow.helper.commands.CountAssertions;
import arrow.helper.commands.ListTestMethods;
import arrow.helper.commands.MethodAtLine;
import arrow.helper.commands.MethodSource;
import arrow.helper.commands.Parse;
import arrow.helper.commands.RenameClass;
import arrow.helper.commands.ShrinkSource;
import arrow.helper.commands.SpliceMethods;
import arrow.helper.commands.StripCommentsHash;
import com.google.gson.JsonObject;
import com.google.gson.JsonParseException;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Vòng lặp của java-helper (spec 07 Mục 10). Chạy thường trú: mỗi dòng stdin là một lệnh JSON
 * {@code {"id": 1, "command": "parse", "args": {...}}}, mỗi lệnh trả đúng một dòng stdout
 * {@code {"id": 1, "ok": true, "result": {...}}} hoặc {@code {"id": 1, "ok": false, "error": "..."}}.
 * Lệnh {@code shutdown} kết thúc chương trình; hết stdin cũng kết thúc.
 */
public final class Main {

    public static final String VERSION = "1.0.0";

    private Main() {
    }

    public static void main(String[] args) throws IOException {
        PrintStream out = new PrintStream(System.out, true, StandardCharsets.UTF_8);
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String line;
        while ((line = in.readLine()) != null) {
            if (line.isBlank()) {
                continue;
            }
            Map<String, Object> response = handle(line);
            out.println(Json.write(response));
            if (Boolean.TRUE.equals(response.get("shutdown"))) {
                return;
            }
        }
    }

    /** Xử lý một dòng lệnh, không bao giờ ném lỗi ra ngoài: lỗi trả về dưới dạng {@code ok: false}. */
    public static Map<String, Object> handle(String line) {
        Map<String, Object> response = new LinkedHashMap<>();
        JsonObject request;
        try {
            request = Json.parse(line);
        } catch (JsonParseException | IllegalStateException e) {
            response.put("id", null);
            response.put("ok", false);
            response.put("error", "Dòng lệnh không phải JSON: " + e.getMessage());
            return response;
        }

        response.put("id", request.has("id") && !request.get("id").isJsonNull() ? request.get("id").getAsLong() : null);
        String command = Json.text(request, "command");
        JsonObject arguments = request.has("args") && request.get("args").isJsonObject() ? request.getAsJsonObject("args") : new JsonObject();
        try {
            Object result = run(command, arguments);
            response.put("ok", true);
            response.put("result", result);
            if (command.equals("shutdown")) {
                response.put("shutdown", true);
            }
        } catch (RuntimeException | StackOverflowError e) {
            response.put("ok", false);
            response.put("error", e.getClass().getSimpleName() + ": " + e.getMessage());
        }
        return response;
    }

    static Object run(String command, JsonObject args) {
        return switch (command) {
            case "ping", "shutdown" -> Map.of("version", VERSION);
            case "parse" -> Parse.run(args);
            case "rename_class" -> RenameClass.run(args);
            case "strip_comments_hash" -> StripCommentsHash.run(args);
            case "count_assertions" -> CountAssertions.run(args);
            case "list_test_methods" -> ListTestMethods.run(args);
            case "method_at_line" -> MethodAtLine.run(args);
            case "splice_methods" -> SpliceMethods.run(args);
            case "class_api" -> ClassApi.run(args);
            case "method_source" -> MethodSource.run(args);
            case "called_methods" -> CalledMethods.run(args);
            case "shrink_source" -> ShrinkSource.run(args);
            default -> throw new IllegalArgumentException("Lệnh không hỗ trợ: " + command);
        };
    }
}
