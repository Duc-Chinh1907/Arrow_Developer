package com.acme.shop;

import com.acme.shop.api.TaxPolicy;

public class OrderService {
    private final TaxPolicy tax;

    public OrderService(TaxPolicy tax) {
        this.tax = tax;
    }

    public long totalPrice(long amount) {
        if (amount < 0) {
            throw new IllegalArgumentException("amount < 0");
        }
        return tax.apply(amount);
    }
}
