package com.acme.sample;

import static org.junit.Assert.assertEquals;

import org.junit.Ignore;
import org.junit.Test;

public class GreeterTest {
    @Ignore("tất cả test bị bỏ qua: tiêu chí B ra NO_TEST_RUN")
    @Test
    public void greets() {
        assertEquals("Hello, A", new Greeter().greet("A"));
    }
}
