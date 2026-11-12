package com.acme.billing;

public class PriceCalculator {
    public int add(int a, int b) {
        return a + b;
    }

    public int divide(int a, int b) {
        if (b == 0) {
            throw new IllegalArgumentException("b must not be 0");
        }
        return a / b;
    }

    public int length(String code) {
        return code.trim().length();
    }
}
