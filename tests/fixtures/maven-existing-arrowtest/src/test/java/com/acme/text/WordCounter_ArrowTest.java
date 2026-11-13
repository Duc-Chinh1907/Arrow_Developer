package com.acme.text;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

/** Written into the project by an earlier ARROW run; a new run must not overwrite it. */
public class WordCounter_ArrowTest {

    @Test
    public void countsRepeatedWords() {
        assertEquals(Integer.valueOf(2), new WordCounter().count("a b a").get("a"));
    }
}
