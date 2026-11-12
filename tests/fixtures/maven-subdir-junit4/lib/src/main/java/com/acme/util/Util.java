package com.acme.util;

public final class Util {
    private Util() {
    }

    public static String prependZeros(int count, String value) {
        StringBuilder text = new StringBuilder();
        for (int i = 0; i < count; i++) {
            text.append('0');
        }
        return text.append(value).toString();
    }
}
