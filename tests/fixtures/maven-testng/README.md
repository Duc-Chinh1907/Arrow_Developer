Fixture: một module Maven, Java 11, test bằng **TestNG** 7.10 (không có JUnit). Dùng để kiểm tra khung DISCOVERY cho
TestNG ở khối 3 và plugin `pitest-testng-plugin` ở khối 4 (P11 §2).

| Mục | Giá trị |
|---|---|
| CUT gợi ý | `com.acme.inventory.Stock` |
| Baseline mong đợi | 2 test pass (`StockTest`), 0 fail, 0 flaky |
| Test framework | `testng` (dependency `org.testng:testng`) |
| JDK | 11 trở lên (TestNG 7.10 cần Java 11) |
| `generated_test_path` mong đợi | `src/test/java/com/acme/inventory/Stock_ArrowTest.java` |
| Bộ test mẫu VALID | `.arrow-sample/Stock_ArrowTest.java`, 5 test |
| Chỉ số khối 4 với bộ mẫu (đo 14/11/2026) | line 90% (19/21), branch 86% (12/14), method 100%; mutation 75% (12/16), test strength 0,80; 18 smell, mật độ 3,6. Bộ mẫu không gọi `add` với số dương và không có trường hợp `isEmpty()` sai |
