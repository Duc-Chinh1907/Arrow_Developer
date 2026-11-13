package com.acme.text;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertNull;
import static org.junit.Assert.assertTrue;

import java.util.Map;
import org.junit.Test;

public class WordCounter_Arrow2Test {

    private final WordCounter counter = new WordCounter();

    @Test
    public void ignoresCaseAndPunctuation() {
        Map<String, Integer> counts = counter.count("Hello, hello! World.");
        assertEquals(Integer.valueOf(2), counts.get("hello"));
        assertEquals(Integer.valueOf(1), counts.get("world"));
        assertEquals(2, counts.size());
    }

    @Test
    public void nullOrEmptyTextHasNoWords() {
        assertTrue(counter.count(null).isEmpty());
        assertTrue(counter.count(" ,; ").isEmpty());
    }

    @Test
    public void mostFrequentWins() {
        assertEquals("b", counter.mostFrequent("b a b c"));
    }

    @Test
    public void tieGoesToTheAlphabeticallyFirstWord() {
        assertEquals("apple", counter.mostFrequent("pear apple pear apple"));
    }

    @Test
    public void textWithoutWordsHasNoMostFrequentWord() {
        assertNull(counter.mostFrequent("..."));
    }
}
