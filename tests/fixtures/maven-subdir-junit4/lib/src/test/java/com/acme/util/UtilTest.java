package com.acme.util;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class UtilTest {
    @Test
    public void prependsZeros() {
        assertEquals("00ab", Util.prependZeros(2, "ab"));
    }

    @Test
    public void zeroCountKeepsValue() {
        assertEquals("ab", Util.prependZeros(0, "ab"));
    }
}
