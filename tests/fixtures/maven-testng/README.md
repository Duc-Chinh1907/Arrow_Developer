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
| Chỉ số khối 4 với bộ mẫu | line và branch 100%; mutation đo và ghi lại ở GĐ5 |
