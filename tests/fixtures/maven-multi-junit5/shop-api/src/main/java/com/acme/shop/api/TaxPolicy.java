package com.acme.shop.api;

public class TaxPolicy {
    private final int percent;

    public TaxPolicy(int percent) {
        this.percent = percent;
    }

    public long apply(long amount) {
        return amount + amount * percent / 100;
    }
}
