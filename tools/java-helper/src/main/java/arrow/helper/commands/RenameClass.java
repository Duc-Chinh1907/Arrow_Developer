package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import arrow.helper.JavaSource.LineIndex;
import com.github.javaparser.Range;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.PackageDeclaration;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.expr.NameExpr;
import com.github.javaparser.ast.expr.SimpleName;
import com.github.javaparser.ast.type.ClassOrInterfaceType;
import com.google.gson.JsonObject;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;

/**
 * {@code rename_class}: chuẩn hóa tên (spec 02 Mục 4.5 bước 3): đặt package và đổi tên class chính cùng mọi chỗ tham chiếu
 * (constructor, kiểu của field/biến/tham số, lời gọi static, {@code new}). Lấy vị trí các tên từ cây cú pháp rồi thay thẳng trên
 * văn bản gốc, nên mọi ký tự khác (định dạng, comment) giữ nguyên.
 * Vào: {@code code}, {@code new_name}, {@code package} (tùy chọn; chuỗi rỗng = không có package).
 * Ra: {@code code}, {@code old_name}, {@code actions}.
 */
public final class RenameClass {

    private RenameClass() {
    }

    private record Edit(int start, int end, String text) {
    }

    public static Map<String, Object> run(JsonObject args) {
        String code = Json.required(args, "code");
        String newName = Json.required(args, "new_name");
        boolean setPackage = args.has("package") && !args.get("package").isJsonNull();
        String wantedPackage = setPackage ? args.get("package").getAsString() : null;

        CompilationUnit unit = JavaSource.parse(code).get();
        TypeDeclaration<?> type = JavaSource.primaryType(unit, newName);
        String oldName = type.getNameAsString();
        LineIndex index = new LineIndex(code);
        List<Edit> edits = new ArrayList<>();
        List<String> actions = new ArrayList<>();

        // 1. Package.
        Optional<PackageDeclaration> current = unit.getPackageDeclaration();
        String currentPackage = current.map(PackageDeclaration::getNameAsString).orElse("");
        if (setPackage && !currentPackage.equals(wantedPackage)) {
            if (current.isPresent()) {
                Range range = current.get().getRange().orElseThrow();
                int start = index.offset(range.begin);
                int end = index.offset(range.end) + 1;
                if (wantedPackage.isEmpty()) {
                    if (end < code.length() && code.charAt(end) == '\n') {
                        end++;
                    }
                    edits.add(new Edit(start, end, ""));
                } else {
                    edits.add(new Edit(start, end, "package " + wantedPackage + ";"));
                }
            } else {
                edits.add(new Edit(0, 0, "package " + wantedPackage + ";\n\n"));
            }
            actions.add(currentPackage.isEmpty() ? "package: added " + wantedPackage : "package: " + currentPackage + " → " + wantedPackage);
        }

        // 2. Tên class và mọi chỗ tham chiếu (chỉ tên không có tiền tố package).
        if (!oldName.equals(newName)) {
            List<SimpleName> names = new ArrayList<>();
            names.add(type.getName());
            for (ConstructorDeclaration constructor : type.findAll(ConstructorDeclaration.class)) {
                if (constructor.getNameAsString().equals(oldName)) {
                    names.add(constructor.getName());
                }
            }
            for (ClassOrInterfaceType reference : unit.findAll(ClassOrInterfaceType.class)) {
                if (reference.getNameAsString().equals(oldName) && reference.getScope().isEmpty()) {
                    names.add(reference.getName());
                }
            }
            for (NameExpr reference : unit.findAll(NameExpr.class)) {
                if (reference.getNameAsString().equals(oldName)) {
                    names.add(reference.getName());
                }
            }
            for (SimpleName name : names) {
                Range range = name.getRange().orElseThrow();
                edits.add(new Edit(index.offset(range.begin), index.offset(range.end) + 1, newName));
            }
            actions.add("class: " + oldName + " → " + newName);
        }

        // Áp dụng từ cuối lên đầu; bỏ vị trí trùng (một tên có thể được nhiều loại node trỏ tới).
        edits.sort(Comparator.comparingInt(Edit::start).reversed());
        StringBuilder result = new StringBuilder(code);
        int last = -1;
        for (Edit edit : edits) {
            if (edit.start() == last && edit.start() != edit.end()) {
                continue;
            }
            result.replace(edit.start(), edit.end(), edit.text());
            last = edit.start();
        }

        Map<String, Object> output = new LinkedHashMap<>();
        output.put("code", result.toString());
        output.put("old_name", oldName);
        output.put("actions", actions);
        return output;
    }
}
