package com.acme.billing;

public class Invoice {
    private static int defaultRate = 10;

    public static void setDefaultRate(int rate) {
        defaultRate = rate;
    }

    public long total(long amount) {
        return amount + amount * defaultRate / 100;
    }
}
