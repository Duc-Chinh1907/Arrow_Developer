```java
package com.acme.billing;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class PriceCalculator_ArrowTest {
    @Test
    public void adds() {
        assertEquals(5, new PriceCalculator().add(2, 3));
    }

    @Test
    public void divides() {
        assertEquals(2, new PriceCalculator().divide(4, 2));
    }
}
```
