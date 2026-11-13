package com.acme.text;

import java.util.HashMap;
import java.util.Locale;
import java.util.Map;

/** Counts words in a text, ignoring case and punctuation. */
public class WordCounter {

    public Map<String, Integer> count(String text) {
        Map<String, Integer> counts = new HashMap<>();
        if (text == null) {
            return counts;
        }
        for (String word : text.toLowerCase(Locale.ROOT).split("[^\\p{L}\\p{N}']+")) {
            if (!word.isEmpty()) {
                counts.merge(word, 1, Integer::sum);
            }
        }
        return counts;
    }

    /** The most frequent word; on a tie, the one that comes first alphabetically. Null for a text without words. */
    public String mostFrequent(String text) {
        String best = null;
        int bestCount = 0;
        for (Map.Entry<String, Integer> entry : count(text).entrySet()) {
            int n = entry.getValue();
            if (n > bestCount || (n == bestCount && entry.getKey().compareTo(best) < 0)) {
                best = entry.getKey();
                bestCount = n;
            }
        }
        return best;
    }
}
