package com.acme.billing;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.fail;

import org.junit.Test;

public class PriceCalculator_ArrowTest {

    @Test
    public void addsTwoNumbers() {
        assertEquals(5, new PriceCalculator().add(2, 3));
        assertEquals(-1, new PriceCalculator().add(2, -3));
    }

    @Test
    public void dividesWithIntegerTruncation() {
        assertEquals(3, new PriceCalculator().divide(10, 3));
    }

    @Test
    public void rejectsZeroDivisor() {
        try {
            new PriceCalculator().divide(1, 0);
            fail("expected IllegalArgumentException");
        } catch (IllegalArgumentException expected) {
            assertEquals("b must not be 0", expected.getMessage());
        }
    }

    @Test
    public void lengthIgnoresSurroundingSpaces() {
        assertEquals(4, new PriceCalculator().length("  code "));
    }
}
