package com.acme.text;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import org.junit.jupiter.api.Test;

public class Slugifier_ArrowTest {

    @Test
    void lowerCasesAndJoinsWordsWithDashes() {
        assertEquals("hello-world", Slugifier.slugify("Hello, World!"));
    }

    @Test
    void removesAccents() {
        assertEquals("xin-chao", Slugifier.slugify("Xin Chào"));
    }

    @Test
    void trimsDashesAtBothEnds() {
        assertEquals("a-b", Slugifier.slugify("  --a  b--  "));
    }

    @Test
    void titleWithoutLettersBecomesUntitled() {
        assertEquals("untitled", Slugifier.slugify("!!!"));
    }

    @Test
    void nullTitleIsRejected() {
        assertThrows(IllegalArgumentException.class, () -> Slugifier.slugify(null));
    }
}
