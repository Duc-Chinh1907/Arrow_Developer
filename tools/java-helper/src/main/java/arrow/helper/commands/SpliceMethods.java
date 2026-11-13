package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import arrow.helper.JavaSource.LineIndex;
import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.ImportDeclaration;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.BodyDeclaration;
import com.github.javaparser.ast.body.FieldDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.body.VariableDeclarator;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.stream.Collectors;

/**
 * {@code splice_methods}: ghép ở mức 3 (spec 02 Mục 4.5 bước 4; AR Mục 3.1). Các method lỗi của checkpoint được thay bằng bản
 * trong câu trả lời; method lỗi mà câu trả lời không có thì bị bỏ; import, field và helper method mới của câu trả lời được thêm.
 * Mọi ký tự khác của checkpoint giữ nguyên: sửa trực tiếp trên văn bản theo vị trí.
 * Vào: {@code base}, {@code response}, {@code methods}. Ra: {@code code}, {@code replaced}, {@code dropped}, {@code added_imports},
 * {@code added_fields}, {@code added_methods}.
 */
public final class SpliceMethods {

    private SpliceMethods() {
    }

    private record Edit(int start, int end, String text) {
    }

    public static Map<String, Object> run(JsonObject args) {
        String base = Json.required(args, "base");
        String response = Json.required(args, "response");
        Set<String> failing = new LinkedHashSet<>(Json.strings(args, "methods"));
        JavaSource.Parsed baseUnit = JavaSource.parse(base);
        if (!baseUnit.ok()) {
            throw new IllegalArgumentException("Checkpoint không parse được: " + baseUnit.error());
        }
        JavaSource.Parsed responseUnit = JavaSource.parse(response);
        if (!responseUnit.ok()) {
            throw new IllegalArgumentException("Câu trả lời không parse được: " + responseUnit.error());
        }
        return splice(base, baseUnit.get(), response, responseUnit.get(), failing);
    }

    static Map<String, Object> splice(String checkpoint, CompilationUnit base, String response, CompilationUnit incoming, Set<String> failing) {
        LineIndex baseIndex = new LineIndex(checkpoint);
        LineIndex responseIndex = new LineIndex(response);
        TypeDeclaration<?> baseType = JavaSource.primaryType(base, null);
        TypeDeclaration<?> responseType = JavaSource.primaryType(incoming, baseType.getNameAsString());
        List<Edit> edits = new ArrayList<>();
        List<String> replaced = new ArrayList<>();
        List<String> dropped = new ArrayList<>();

        // 1. Thay hoặc bỏ các method lỗi (mọi bản overload cùng tên).
        for (String name : failing) {
            List<MethodDeclaration> current = directMethods(baseType, name);
            if (current.isEmpty()) {
                continue;
            }
            List<MethodDeclaration> fresh = directMethods(responseType, name);
            for (int i = 0; i < current.size(); i++) {
                MethodDeclaration method = current.get(i);
                if (i == 0 && !fresh.isEmpty()) {
                    int start = baseIndex.offset(JavaSource.withComment(method).begin);
                    int end = baseIndex.offset(method.getRange().orElseThrow().end) + 1;
                    String indent = indentOf(checkpoint, start);
                    String text = fresh.stream().map(m -> responseIndex.slice(JavaSource.withComment(m))).collect(Collectors.joining("\n\n" + indent));
                    edits.add(new Edit(start, end, text));
                } else {
                    edits.add(removal(checkpoint, baseIndex, method));
                }
            }
            (fresh.isEmpty() ? dropped : replaced).add(name);
        }

        // 2. Import mới: thêm sau import cuối (hoặc sau package).
        Set<String> baseImports = base.getImports().stream().map(SpliceMethods::importKey).collect(Collectors.toSet());
        List<String> addedImports = new ArrayList<>();
        for (ImportDeclaration declaration : incoming.getImports()) {
            if (!baseImports.contains(importKey(declaration))) {
                addedImports.add(responseIndex.slice(declaration.getRange().orElseThrow()).trim());
            }
        }
        if (!addedImports.isEmpty()) {
            int at;
            if (!base.getImports().isEmpty()) {
                at = baseIndex.offset(base.getImports().get(base.getImports().size() - 1).getRange().orElseThrow().end) + 1;
            } else if (base.getPackageDeclaration().isPresent()) {
                at = baseIndex.offset(base.getPackageDeclaration().get().getRange().orElseThrow().end) + 1;
            } else {
                at = 0;
            }
            String block = addedImports.stream().map(i -> "\n" + i).collect(Collectors.joining());
            edits.add(new Edit(at, at, at == 0 ? block.substring(1) + "\n" : block));
        }

        // 3. Field và helper method mới: thêm trước dấu } đóng class.
        Set<String> baseFields = new HashSet<>();
        for (FieldDeclaration field : directMembers(baseType, FieldDeclaration.class)) {
            field.getVariables().forEach(v -> baseFields.add(v.getNameAsString()));
        }
        Set<String> baseSignatures = new HashSet<>();
        for (MethodDeclaration method : directMembers(baseType, MethodDeclaration.class)) {
            baseSignatures.add(method.getSignature().asString());
        }
        List<String> addedFields = new ArrayList<>();
        List<String> addedMethods = new ArrayList<>();
        List<String> additions = new ArrayList<>();
        for (BodyDeclaration<?> member : responseType.getMembers()) {
            if (member instanceof FieldDeclaration field) {
                List<String> names = field.getVariables().stream().map(VariableDeclarator::getNameAsString).toList();
                if (names.stream().noneMatch(baseFields::contains)) {
                    addedFields.addAll(names);
                    additions.add(responseIndex.slice(JavaSource.withComment(field)));
                }
            } else if (member instanceof MethodDeclaration method) {
                if (!JavaSource.isTest(method) && !failing.contains(method.getNameAsString())
                        && !baseSignatures.contains(method.getSignature().asString())) {
                    addedMethods.add(method.getNameAsString());
                    additions.add(responseIndex.slice(JavaSource.withComment(method)));
                }
            }
        }
        if (!additions.isEmpty()) {
            int close = baseIndex.offset(baseType.getRange().orElseThrow().end);
            String indent = baseType.getMembers().isEmpty()
                    ? "    "
                    : indentOf(checkpoint, baseIndex.offset(baseType.getMember(0).getRange().orElseThrow().begin));
            StringBuilder block = new StringBuilder();
            for (String addition : additions) {
                block.append('\n').append(indent).append(addition).append('\n');
            }
            int lineStart = checkpoint.lastIndexOf('\n', close - 1) + 1;
            boolean braceAlone = checkpoint.substring(lineStart, close).trim().isEmpty();
            edits.add(braceAlone ? new Edit(lineStart, lineStart, block.substring(1)) : new Edit(close, close, block.toString()));
        }

        // Áp dụng từ cuối lên đầu để vị trí của các sửa đổi phía trước không bị lệch.
        edits.sort(Comparator.comparingInt(Edit::start).reversed());
        StringBuilder code = new StringBuilder(checkpoint);
        int limit = Integer.MAX_VALUE;
        for (Edit edit : edits) {
            if (edit.end() > limit) {
                continue;
            }
            code.replace(edit.start(), edit.end(), edit.text());
            limit = edit.start();
        }

        Map<String, Object> result = new LinkedHashMap<>();
        result.put("code", code.toString());
        result.put("replaced", replaced);
        result.put("dropped", dropped);
        result.put("added_imports", addedImports);
        result.put("added_fields", addedFields);
        result.put("added_methods", addedMethods);
        return result;
    }

    private static List<MethodDeclaration> directMethods(TypeDeclaration<?> type, String name) {
        return directMembers(type, MethodDeclaration.class).stream().filter(m -> m.getNameAsString().equals(name)).toList();
    }

    private static <T extends BodyDeclaration<?>> List<T> directMembers(TypeDeclaration<?> type, Class<T> kind) {
        return type.getMembers().stream().filter(kind::isInstance).map(kind::cast).toList();
    }

    private static String importKey(ImportDeclaration declaration) {
        return (declaration.isStatic() ? "static " : "") + declaration.getNameAsString() + (declaration.isAsterisk() ? ".*" : "");
    }

    /** Xóa một method cùng dòng trống phía trên nó, không để lại dòng trống thừa. */
    private static Edit removal(String text, LineIndex index, Node node) {
        Range range = JavaSource.withComment(node);
        int start = index.offset(range.begin);
        int end = index.offset(range.end) + 1;
        int lineStart = text.lastIndexOf('\n', start - 1) + 1;
        if (text.substring(lineStart, start).trim().isEmpty()) {
            start = lineStart;
        }
        if (end < text.length() && text.charAt(end) == '\n') {
            end++;
        }
        if (start >= 2 && text.charAt(start - 1) == '\n' && text.substring(text.lastIndexOf('\n', start - 2) + 1, start - 1).trim().isEmpty()) {
            start = text.lastIndexOf('\n', start - 2) + 1;
        }
        return new Edit(start, end, "");
    }

    private static String indentOf(String text, int offset) {
        int lineStart = text.lastIndexOf('\n', offset - 1) + 1;
        String prefix = text.substring(lineStart, offset);
        return prefix.trim().isEmpty() ? prefix : "    ";
    }
}
