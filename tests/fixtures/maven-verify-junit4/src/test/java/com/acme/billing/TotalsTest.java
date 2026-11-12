package com.acme.billing;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class TotalsTest {
    @Test
    public void appliesDefaultRate() {
        assertEquals(110, new Invoice().total(100));
    }
}
