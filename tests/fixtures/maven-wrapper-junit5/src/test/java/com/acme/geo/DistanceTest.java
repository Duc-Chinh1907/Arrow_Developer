package com.acme.geo;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

/** Test written by the project developers. */
class DistanceTest {

    @Test
    void samePointIsZero() {
        assertEquals(0.0, Distance.kilometres(21.03, 105.85, 21.03, 105.85), 1e-9);
    }
}
