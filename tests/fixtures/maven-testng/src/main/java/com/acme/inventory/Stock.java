package com.acme.inventory;

/** Quantity on hand of one stock keeping unit. */
public class Stock {

    private final String sku;
    private int quantity;

    public Stock(String sku, int quantity) {
        if (sku == null || sku.isBlank()) {
            throw new IllegalArgumentException("sku must not be blank");
        }
        if (quantity < 0) {
            throw new IllegalArgumentException("quantity must not be negative");
        }
        this.sku = sku;
        this.quantity = quantity;
    }

    public void add(int amount) {
        if (amount <= 0) {
            throw new IllegalArgumentException("amount must be positive");
        }
        quantity += amount;
    }

    public void remove(int amount) {
        if (amount <= 0) {
            throw new IllegalArgumentException("amount must be positive");
        }
        if (amount > quantity) {
            throw new IllegalStateException("not enough stock for " + sku);
        }
        quantity -= amount;
    }

    public boolean isEmpty() {
        return quantity == 0;
    }

    public String getSku() {
        return sku;
    }

    public int getQuantity() {
        return quantity;
    }
}
