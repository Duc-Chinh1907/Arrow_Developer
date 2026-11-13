package arrow.helper;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.List;

/** Đọc/ghi JSON của giao thức (Gson). Một dòng mỗi thông điệp nên không xuống dòng khi ghi. */
public final class Json {

    private static final Gson GSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();

    private Json() {
    }

    public static JsonObject parse(String line) {
        return GSON.fromJson(line, JsonObject.class);
    }

    public static String write(Object value) {
        return GSON.toJson(value);
    }

    /** Chuỗi của trường {@code name}; thiếu hoặc null thì chuỗi rỗng. */
    public static String text(JsonObject object, String name) {
        JsonElement value = object.get(name);
        return value == null || value.isJsonNull() ? "" : value.getAsString();
    }

    /** Chuỗi bắt buộc; thiếu thì ném lỗi rõ tên trường. */
    public static String required(JsonObject object, String name) {
        JsonElement value = object.get(name);
        if (value == null || value.isJsonNull()) {
            throw new IllegalArgumentException("Thiếu tham số " + name);
        }
        return value.getAsString();
    }

    public static List<String> strings(JsonObject object, String name) {
        List<String> values = new ArrayList<>();
        JsonElement value = object.get(name);
        if (value != null && value.isJsonArray()) {
            value.getAsJsonArray().forEach(element -> values.add(element.getAsString()));
        }
        return values;
    }

    public static List<Integer> ints(JsonObject object, String name) {
        List<Integer> values = new ArrayList<>();
        JsonElement value = object.get(name);
        if (value != null && value.isJsonArray()) {
            value.getAsJsonArray().forEach(element -> values.add(element.getAsInt()));
        }
        return values;
    }
}
