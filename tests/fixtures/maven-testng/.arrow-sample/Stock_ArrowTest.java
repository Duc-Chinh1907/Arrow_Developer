package com.acme.inventory;

import static org.testng.Assert.assertEquals;
import static org.testng.Assert.assertThrows;
import static org.testng.Assert.assertTrue;

import org.testng.annotations.Test;

public class Stock_ArrowTest {

    @Test
    public void keepsSkuAndQuantity() {
        Stock stock = new Stock("SKU-9", 4);
        assertEquals(stock.getSku(), "SKU-9");
        assertEquals(stock.getQuantity(), 4);
    }

    @Test
    public void removeDownToZeroMakesItEmpty() {
        Stock stock = new Stock("SKU-9", 4);
        stock.remove(4);
        assertTrue(stock.isEmpty());
    }

    @Test
    public void removingMoreThanAvailableFails() {
        Stock stock = new Stock("SKU-9", 1);
        assertThrows(IllegalStateException.class, () -> stock.remove(2));
        assertEquals(stock.getQuantity(), 1);
    }

    @Test
    public void amountsMustBePositive() {
        Stock stock = new Stock("SKU-9", 1);
        assertThrows(IllegalArgumentException.class, () -> stock.add(0));
        assertThrows(IllegalArgumentException.class, () -> stock.remove(-1));
    }

    @Test
    public void constructorRejectsBadValues() {
        assertThrows(IllegalArgumentException.class, () -> new Stock(null, 1));
        assertThrows(IllegalArgumentException.class, () -> new Stock("  ", 1));
        assertThrows(IllegalArgumentException.class, () -> new Stock("SKU-9", -1));
    }
}
