package arrow.helper.commands;

import arrow.helper.Json;
import com.github.javaparser.JavaParser;
import com.github.javaparser.ParseResult;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.Problem;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.AnnotationDeclaration;
import com.github.javaparser.ast.body.ClassOrInterfaceDeclaration;
import com.github.javaparser.ast.body.EnumDeclaration;
import com.github.javaparser.ast.body.RecordDeclaration;
import com.github.javaparser.ast.body.TypeDeclaration;
import com.google.gson.JsonObject;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;

/**
 * {@code list_classes} (P10 §4.13, new in 1.1.0): the top-level types of every {@code .java} file under the given
 * source roots, for {@code ClassLocator} (P01 §3.3) and the class picker of the interactive session (P08 §5.3).
 * Vào: {@code roots}, {@code max_files} (default 20000). Ra: {@code classes}, {@code errors}, {@code truncated}.
 * {@code package-info.java} and {@code module-info.java} are skipped; a file that does not parse goes to {@code errors}.
 */
public final class ListClasses {

    static final int DEFAULT_MAX_FILES = 20_000;

    private ListClasses() {
    }

    public static Map<String, Object> run(JsonObject args) {
        List<String> roots = Json.strings(args, "roots");
        int maxFiles = args.has("max_files") && !args.get("max_files").isJsonNull() ? args.get("max_files").getAsInt() : DEFAULT_MAX_FILES;

        List<Map<String, Object>> errors = new ArrayList<>();
        List<Path[]> work = new ArrayList<>();
        boolean truncated = false;
        outer:
        for (String rootText : roots) {
            Path root = Path.of(rootText).toAbsolutePath().normalize();
            if (!Files.isDirectory(root)) {
                errors.add(error(root, "not a directory"));
                continue;
            }
            for (Path file : javaFiles(root)) {
                if (work.size() >= maxFiles) {
                    truncated = true;
                    break outer;
                }
                work.add(new Path[] {file, root});
            }
        }

        // Files are independent: parse them in parallel, one parser per thread, and keep the input order in the output.
        List<FileResult> results = work.parallelStream().map(item -> read(item[0], item[1])).toList();
        List<Map<String, Object>> classes = new ArrayList<>();
        for (FileResult fileResult : results) {
            classes.addAll(fileResult.classes());
            if (fileResult.error() != null) {
                errors.add(fileResult.error());
            }
        }

        Map<String, Object> result = new LinkedHashMap<>();
        result.put("classes", classes);
        result.put("errors", errors);
        result.put("truncated", truncated);
        return result;
    }

    private record FileResult(List<Map<String, Object>> classes, Map<String, Object> error) {
    }

    /**
     * Declarations only, tuned for speed (P10 §4.13, target 5 000 files in 10 s): no comments attached, no token list kept,
     * and the RAW language level, which reads every syntax up to Java 21 but skips the validators of the other levels.
     */
    private static final ThreadLocal<JavaParser> PARSER = ThreadLocal.withInitial(() -> new JavaParser(new ParserConfiguration()
            .setLanguageLevel(ParserConfiguration.LanguageLevel.RAW)
            .setAttributeComments(false)
            .setStoreTokens(false)));

    private static FileResult read(Path file, Path root) {
        try {
            ParseResult<CompilationUnit> result = PARSER.get().parse(Files.readString(file, StandardCharsets.UTF_8));
            if (!result.isSuccessful() || result.getResult().isEmpty()) {
                String message = result.getProblems().stream().findFirst().map(Problem::getVerboseMessage).orElse("parse error");
                return new FileResult(List.of(), error(file, message.lines().findFirst().orElse(message)));
            }
            CompilationUnit unit = result.getResult().get();
            String pkg = unit.getPackageDeclaration().map(p -> p.getNameAsString()).orElse("");
            List<Map<String, Object>> classes = new ArrayList<>();
            for (TypeDeclaration<?> type : unit.getTypes()) {
                classes.add(describe(type, pkg, file, root));
            }
            return new FileResult(classes, null);
        } catch (IOException | UncheckedIOException e) {
            return new FileResult(List.of(), error(file, e.getClass().getSimpleName() + ": " + e.getMessage()));
        } catch (RuntimeException | StackOverflowError e) {
            return new FileResult(List.of(), error(file, e.getClass().getSimpleName() + ": " + e.getMessage()));
        }
    }

    /** Every {@code .java} file below {@code root}, sorted for a stable order, without package-info and module-info. */
    private static List<Path> javaFiles(Path root) {
        try (Stream<Path> walk = Files.walk(root)) {
            return walk.filter(Files::isRegularFile)
                    .filter(p -> p.getFileName().toString().endsWith(".java"))
                    .filter(p -> !p.getFileName().toString().equals("package-info.java") && !p.getFileName().toString().equals("module-info.java"))
                    .sorted()
                    .toList();
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }

    private static Map<String, Object> describe(TypeDeclaration<?> type, String pkg, Path file, Path root) {
        String name = type.getNameAsString();
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("fqn", pkg.isEmpty() ? name : pkg + "." + name);
        entry.put("simple_name", name);
        entry.put("kind", kind(type));
        entry.put("is_abstract", type instanceof ClassOrInterfaceDeclaration c && !c.isInterface() && c.isAbstract());
        entry.put("is_public", type.isPublic());
        entry.put("path", file.toString());
        entry.put("root", root.toString());
        return entry;
    }

    private static String kind(TypeDeclaration<?> type) {
        if (type instanceof ClassOrInterfaceDeclaration c) {
            return c.isInterface() ? "interface" : "class";
        }
        if (type instanceof EnumDeclaration) {
            return "enum";
        }
        if (type instanceof RecordDeclaration) {
            return "record";
        }
        if (type instanceof AnnotationDeclaration) {
            return "annotation";
        }
        return "class";
    }

    private static Map<String, Object> error(Path path, String message) {
        Map<String, Object> entry = new LinkedHashMap<>();
        entry.put("path", path.toString());
        entry.put("error", message);
        return entry;
    }
}
