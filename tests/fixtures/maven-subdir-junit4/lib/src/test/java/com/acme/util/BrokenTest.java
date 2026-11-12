package com.acme.util;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class BrokenTest {
    @Test
    public void alwaysFails() {
        assertEquals("x", Util.prependZeros(1, "x"));
    }
}
