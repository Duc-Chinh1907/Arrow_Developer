package com.acme.text;

import java.text.Normalizer;
import java.util.Locale;

/** Turns a title into a URL slug, e.g. "Hello, World!" into "hello-world". */
public final class Slugifier {

    private Slugifier() {
    }

    public static String slugify(String title) {
        if (title == null) {
            throw new IllegalArgumentException("title must not be null");
        }
        String plain = Normalizer.normalize(title, Normalizer.Form.NFD).replaceAll("\\p{M}", "");
        String slug = plain.toLowerCase(Locale.ROOT)
                .replaceAll("[^a-z0-9]+", "-")
                .replaceAll("(^-|-$)", "");
        return slug.isEmpty() ? "untitled" : slug;
    }
}
