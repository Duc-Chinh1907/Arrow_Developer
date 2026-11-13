package com.acme.inventory;

import static org.testng.Assert.assertEquals;
import static org.testng.Assert.assertFalse;

import org.testng.annotations.Test;

/** Test written by the project developers. */
public class StockTest {

    @Test
    public void addIncreasesQuantity() {
        Stock stock = new Stock("SKU-1", 2);
        stock.add(3);
        assertEquals(stock.getQuantity(), 5);
    }

    @Test
    public void newStockWithItemsIsNotEmpty() {
        assertFalse(new Stock("SKU-2", 1).isEmpty());
    }
}
