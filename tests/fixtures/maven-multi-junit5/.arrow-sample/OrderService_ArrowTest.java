package com.acme.shop;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.acme.shop.api.TaxPolicy;
import org.junit.jupiter.api.Test;

class OrderService_ArrowTest {

    @Test
    void addsTheTaxOfThePolicy() {
        assertEquals(120, new OrderService(new TaxPolicy(20)).totalPrice(100));
    }

    @Test
    void zeroAmountStaysZero() {
        assertEquals(0, new OrderService(new TaxPolicy(20)).totalPrice(0));
    }

    @Test
    void negativeAmountsAreRejected() {
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class, () -> new OrderService(new TaxPolicy(5)).totalPrice(-1));
        assertEquals("amount < 0", error.getMessage());
    }
}
