Fixture: một module Maven, Java 8, có dependency JUnit 5 nhưng **không có test nào** (không có `src/test/java`).
Khối 1 vẫn phải ra `OK` với baseline rỗng (P11 §2).

| Mục | Giá trị |
|---|---|
| CUT gợi ý | `com.acme.text.Slugifier` |
| Baseline mong đợi | 0 test (0 pass, 0 fail, 0 flaky), lệnh build thành công |
| Test framework | `junit5` (từ dependency, không có import nào để đếm) |
| `generated_test_path` mong đợi | `src/test/java/com/acme/text/Slugifier_ArrowTest.java` |
| Bộ test mẫu VALID | `.arrow-sample/Slugifier_ArrowTest.java`, 5 test |
| Chỉ số khối 4 với bộ mẫu | branch 100%; line gần 100% (chỉ constructor `private` không chạy); số chính xác và mutation đo, ghi lại ở GĐ5 |
