package com.acme.calc;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

class Calculator_ArrowTest {

    @Test
    void addsPositiveNumbers() {
        assertEquals(7, new Calculator().add(3, 4));
    }

    @Test
    void addsNegativeNumbers() {
        assertEquals(-7, new Calculator().add(-3, -4));
    }
}
