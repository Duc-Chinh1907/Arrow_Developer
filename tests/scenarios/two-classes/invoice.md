```java
package com.acme.billing;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class Invoice_ArrowTest {
    @Test
    public void addsTheDefaultRate() {
        assertEquals(110, new Invoice().total(100));
    }

    @Test
    public void zeroStaysZero() {
        assertEquals(0, new Invoice().total(0));
    }
}
```
