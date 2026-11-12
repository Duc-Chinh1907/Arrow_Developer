package com.acme.billing;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class PriceCalculatorTest {
    @Test
    public void adds() {
        assertEquals(5, new PriceCalculator().add(2, 3));
    }

    @Test(expected = IllegalArgumentException.class)
    public void rejectsZeroDivisor() {
        new PriceCalculator().divide(1, 0);
    }
}
