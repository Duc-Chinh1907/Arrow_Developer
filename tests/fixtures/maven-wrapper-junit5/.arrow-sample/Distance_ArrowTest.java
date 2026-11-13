package com.acme.geo;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import org.junit.jupiter.api.Test;

public class Distance_ArrowTest {

    @Test
    void hanoiToHoChiMinhCityIsAbout1140Km() {
        assertEquals(1140.0, Distance.kilometres(21.0285, 105.8542, 10.8231, 106.6297), 15.0);
    }

    @Test
    void quarterOfTheEquator() {
        assertEquals(Math.PI * Distance.EARTH_RADIUS_KM / 2, Distance.kilometres(0, 0, 0, 90), 1e-6);
    }

    @Test
    void poleToPoleIsHalfACircle() {
        assertEquals(Math.PI * Distance.EARTH_RADIUS_KM, Distance.kilometres(90, 0, -90, 0), 1e-6);
    }

    @Test
    void latitudeOutOfRangeIsRejected() {
        assertThrows(IllegalArgumentException.class, () -> Distance.kilometres(91, 0, 0, 0));
        assertThrows(IllegalArgumentException.class, () -> Distance.kilometres(0, 0, -91, 0));
    }

    @Test
    void longitudeOutOfRangeIsRejected() {
        assertThrows(IllegalArgumentException.class, () -> Distance.kilometres(0, 181, 0, 0));
        assertThrows(IllegalArgumentException.class, () -> Distance.kilometres(0, 0, 0, -181));
    }
}
