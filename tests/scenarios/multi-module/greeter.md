```java
package com.acme.sample;

import static org.junit.Assert.assertEquals;

import org.junit.Test;

public class Greeter_ArrowTest {
    @Test
    public void greetsByName() {
        assertEquals("Hello, Ann", new Greeter().greet("Ann"));
    }
}
```
