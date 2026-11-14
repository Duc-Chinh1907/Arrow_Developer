```java
package com.acme.shop.api;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

class TaxPolicy_ArrowTest {
    @Test
    void addsThePercent() {
        assertEquals(120, new TaxPolicy(20).apply(100));
    }

    @Test
    void zeroPercentKeepsTheAmount() {
        assertEquals(100, new TaxPolicy(0).apply(100));
    }
}
```
