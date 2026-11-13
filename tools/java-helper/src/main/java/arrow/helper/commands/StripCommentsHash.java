package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import com.google.gson.JsonObject;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.HexFormat;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * {@code strip_comments_hash}: mã đã bỏ comment và thu khoảng trắng, cùng hash của nó (AR Mục 2.3; spec 02 Mục 4.5 bước 5).
 * Hai đoạn mã chỉ khác comment, xuống dòng, thụt lề cho cùng hash. Vào: {@code code}. Ra: {@code parsed}, {@code normalized}, {@code code_hash}.
 */
public final class StripCommentsHash {

    private StripCommentsHash() {
    }

    public static Map<String, Object> run(JsonObject args) {
        String code = Json.required(args, "code").replace("\r\n", "\n");
        String normalized = normalize(code);
        Map<String, Object> result = new LinkedHashMap<>();
        result.put("parsed", JavaSource.parse(code).ok());
        result.put("normalized", normalized);
        result.put("code_hash", "sha256:" + sha256(normalized));
        return result;
    }

    public static String normalize(String code) {
        return JavaSource.collapseWhitespace(JavaSource.stripComments(code));
    }

    static String sha256(String text) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(text.getBytes(StandardCharsets.UTF_8)));
        } catch (NoSuchAlgorithmException e) {
            throw new IllegalStateException(e);
        }
    }
}
