package com.acme.shop;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.acme.shop.api.TaxPolicy;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Nested;
import org.junit.jupiter.api.Test;

class OrderServiceTest {
    @Test
    void addsTax() {
        assertEquals(110, new OrderService(new TaxPolicy(10)).totalPrice(100));
    }

    @Test
    @DisplayName("negative amounts are rejected")
    void rejectsNegative() {
        assertThrows(IllegalArgumentException.class, () -> new OrderService(new TaxPolicy(10)).totalPrice(-1));
    }

    @Nested
    class WithoutTax {
        @Test
        void keepsAmount() {
            assertEquals(100, new OrderService(new TaxPolicy(0)).totalPrice(100));
        }
    }
}
