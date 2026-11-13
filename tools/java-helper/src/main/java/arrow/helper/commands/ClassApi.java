package arrow.helper.commands;

import arrow.helper.Json;
import arrow.helper.JavaSource;
import com.github.javaparser.ast.Node;
import com.github.javaparser.ast.body.BodyDeclaration;
import com.github.javaparser.ast.body.CallableDeclaration;
import com.github.javaparser.ast.body.ConstructorDeclaration;
import com.github.javaparser.ast.body.FieldDeclaration;
import com.github.javaparser.ast.body.MethodDeclaration;
import com.github.javaparser.ast.body.Parameter;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.github.javaparser.ast.body.VariableDeclarator;
import com.google.gson.JsonObject;
import java.io.File;
import java.io.IOException;
import java.lang.reflect.Constructor;
import java.lang.reflect.Executable;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.Type;
import java.net.MalformedURLException;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

/**
 * {@code class_api}: chữ ký constructor, method, field không private của các class (thông tin bổ sung mức 2 COMPILE, spec 03 Mục 6.5).
 * Hai nguồn: mã nguồn ({@code sources}: class của dự án) và classpath ({@code classes} + {@code classpath}: class của thư viện, đọc bằng
 * reflection mà không khởi tạo class). Ra: {@code types} (tên, loại, nguồn, members), {@code missing} (class không tìm được, kèm lý do).
 */
public final class ClassApi {

    private ClassApi() {
    }

    public static Map<String, Object> run(JsonObject args) {
        List<Map<String, Object>> types = new ArrayList<>();
        for (String source : Json.strings(args, "sources")) {
            JavaSource.Parsed parsed = JavaSource.parse(source);
            if (!parsed.ok()) {
                continue;
            }
            for (TypeDeclaration<?> type : parsed.get().findAll(TypeDeclaration.class)) {
                types.add(fromSource(type));
            }
        }

        List<Map<String, Object>> missing = new ArrayList<>();
        List<String> classes = Json.strings(args, "classes");
        if (!classes.isEmpty()) {
            try (URLClassLoader loader = new URLClassLoader(urls(Json.strings(args, "classpath")), ClassLoader.getPlatformClassLoader())) {
                for (String name : classes) {
                    try {
                        types.add(fromClass(Class.forName(name, false, loader)));
                    } catch (ClassNotFoundException | LinkageError | SecurityException e) {
                        Map<String, Object> entry = new LinkedHashMap<>();
                        entry.put("name", name);
                        entry.put("reason", e.getClass().getSimpleName() + ": " + e.getMessage());
                        missing.add(entry);
                    }
                }
            } catch (IOException e) {
                throw new IllegalStateException("Không đóng được class loader: " + e.getMessage(), e);
            }
        }

        Map<String, Object> result = new LinkedHashMap<>();
        result.put("types", types);
        result.put("missing", missing);
        return result;
    }

    // ── từ mã nguồn ────────────────────────────────────────────────────

    private static Map<String, Object> fromSource(TypeDeclaration<?> type) {
        List<String> members = new ArrayList<>();
        for (BodyDeclaration<?> member : type.getMembers()) {
            if (member instanceof ConstructorDeclaration constructor && !constructor.isPrivate()) {
                members.add(signature(constructor));
            } else if (member instanceof MethodDeclaration method && !method.isPrivate()) {
                members.add(signature(method));
            } else if (member instanceof FieldDeclaration field && !field.isPrivate()) {
                for (VariableDeclarator variable : field.getVariables()) {
                    members.add(modifiers(field.getModifiers().stream().map(m -> m.getKeyword().asString()).toList())
                            + variable.getTypeAsString() + " " + variable.getNameAsString());
                }
            }
        }
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("name", type.getFullyQualifiedName().orElse(type.getNameAsString()));
        entry.put("kind", kindOf(type));
        entry.put("origin", "source");
        entry.put("members", members);
        return entry;
    }

    static String signature(CallableDeclaration<?> callable) {
        StringBuilder builder = new StringBuilder(modifiers(callable.getModifiers().stream().map(m -> m.getKeyword().asString()).toList()));
        if (callable instanceof MethodDeclaration method) {
            if (!method.getTypeParameters().isEmpty()) {
                builder.append('<').append(method.getTypeParameters().stream().map(Node::toString).collect(Collectors.joining(", "))).append("> ");
            }
            builder.append(method.getTypeAsString()).append(' ');
        }
        builder.append(callable.getNameAsString()).append('(')
                .append(callable.getParameters().stream().map(Parameter::toString).collect(Collectors.joining(", ")))
                .append(')');
        if (!callable.getThrownExceptions().isEmpty()) {
            builder.append(" throws ").append(callable.getThrownExceptions().stream().map(Node::toString).collect(Collectors.joining(", ")));
        }
        return builder.toString();
    }

    private static String kindOf(TypeDeclaration<?> type) {
        if (type.isClassOrInterfaceDeclaration()) {
            return type.asClassOrInterfaceDeclaration().isInterface() ? "interface" : "class";
        }
        if (type.isEnumDeclaration()) {
            return "enum";
        }
        return type.isRecordDeclaration() ? "record" : "annotation";
    }

    // ── từ classpath (reflection, không khởi tạo class) ──────────────────

    private static Map<String, Object> fromClass(Class<?> type) {
        List<String> members = new ArrayList<>();
        Constructor<?>[] constructors = type.getDeclaredConstructors();
        Arrays.sort(constructors, Comparator.comparing(Constructor::toGenericString));
        for (Constructor<?> constructor : constructors) {
            if (!Modifier.isPrivate(constructor.getModifiers()) && !constructor.isSynthetic()) {
                members.add(signature(constructor, type.getSimpleName(), null));
            }
        }
        Method[] methods = type.getDeclaredMethods();
        Arrays.sort(methods, Comparator.comparing(Method::getName).thenComparing(Method::toGenericString));
        for (Method method : methods) {
            if (!Modifier.isPrivate(method.getModifiers()) && !method.isSynthetic() && !method.isBridge()) {
                members.add(signature(method, method.getName(), method.getGenericReturnType()));
            }
        }
        Field[] fields = type.getDeclaredFields();
        Arrays.sort(fields, Comparator.comparing(Field::getName));
        for (Field field : fields) {
            if (!Modifier.isPrivate(field.getModifiers()) && !field.isSynthetic()) {
                members.add(modifierText(field.getModifiers()) + shortName(field.getGenericType()) + " " + field.getName());
            }
        }
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("name", type.getName());
        entry.put("kind", type.isInterface() ? (type.isAnnotation() ? "annotation" : "interface") : type.isEnum() ? "enum" : type.isRecord() ? "record" : "class");
        entry.put("origin", "classpath");
        entry.put("members", members);
        return entry;
    }

    private static String signature(Executable executable, String name, Type returnType) {
        StringBuilder builder = new StringBuilder(modifierText(executable.getModifiers()));
        if (returnType != null) {
            builder.append(shortName(returnType)).append(' ');
        }
        builder.append(name).append('(')
                .append(Arrays.stream(executable.getGenericParameterTypes()).map(ClassApi::shortName).collect(Collectors.joining(", ")))
                .append(')');
        if (executable.getGenericExceptionTypes().length > 0) {
            builder.append(" throws ").append(Arrays.stream(executable.getGenericExceptionTypes()).map(ClassApi::shortName).collect(Collectors.joining(", ")));
        }
        return builder.toString();
    }

    /** Tên kiểu bỏ tiền tố {@code java.lang.} cho gọn. */
    private static String shortName(Type type) {
        return type.getTypeName().replace("java.lang.", "");
    }

    private static String modifierText(int modifiers) {
        String text = Modifier.toString(modifiers & (Modifier.PUBLIC | Modifier.PROTECTED | Modifier.STATIC | Modifier.FINAL | Modifier.ABSTRACT));
        return text.isEmpty() ? "" : text + " ";
    }

    private static String modifiers(List<String> keywords) {
        return keywords.isEmpty() ? "" : String.join(" ", keywords) + " ";
    }

    private static URL[] urls(List<String> classpath) {
        List<URL> urls = new ArrayList<>();
        for (String entry : classpath) {
            try {
                urls.add(new File(entry).toURI().toURL());
            } catch (MalformedURLException e) {
                throw new IllegalArgumentException("Đường dẫn classpath không hợp lệ: " + entry, e);
            }
        }
        return urls.toArray(URL[]::new);
    }
}
