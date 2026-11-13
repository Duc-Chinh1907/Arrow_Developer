package com.acme.geo;

/** Great-circle distance between two points on Earth (haversine formula). */
public final class Distance {

    /** Mean Earth radius in kilometres. */
    public static final double EARTH_RADIUS_KM = 6371.0;

    private Distance() {
    }

    public static double kilometres(double lat1, double lon1, double lat2, double lon2) {
        checkLatitude(lat1);
        checkLatitude(lat2);
        checkLongitude(lon1);
        checkLongitude(lon2);
        double dLat = Math.toRadians(lat2 - lat1);
        double dLon = Math.toRadians(lon2 - lon1);
        double a = Math.sin(dLat / 2) * Math.sin(dLat / 2)
                + Math.cos(Math.toRadians(lat1)) * Math.cos(Math.toRadians(lat2)) * Math.sin(dLon / 2) * Math.sin(dLon / 2);
        return 2 * EARTH_RADIUS_KM * Math.asin(Math.min(1.0, Math.sqrt(a)));
    }

    private static void checkLatitude(double latitude) {
        if (latitude < -90 || latitude > 90) {
            throw new IllegalArgumentException("latitude out of range: " + latitude);
        }
    }

    private static void checkLongitude(double longitude) {
        if (longitude < -180 || longitude > 180) {
            throw new IllegalArgumentException("longitude out of range: " + longitude);
        }
    }
}
