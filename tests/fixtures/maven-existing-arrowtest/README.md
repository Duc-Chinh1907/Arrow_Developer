Fixture: một module Maven, Java 8, JUnit 4, **đã có `WordCounter_ArrowTest.java`** do một lần chạy ARROW trước ghi vào.
Lần chạy mới phải đặt tên `WordCounter_Arrow2Test`, hỏi người dùng trước khi ghi, và không bao giờ ghi đè file cũ
(P01 §4.7, P06, P11 §2).

| Mục | Giá trị |
|---|---|
| CUT gợi ý | `com.acme.text.WordCounter` |
| Baseline mong đợi | 1 test pass (`WordCounter_ArrowTest`), 0 fail, 0 flaky |
| `generated_test_path` mong đợi | `src/test/java/com/acme/text/WordCounter_Arrow2Test.java`, `name_index = 2` |
| Mẫu tên test của Surefire | mặc định; `WordCounter_Arrow2Test` khớp `**/*Test.java`, nên `name_matches_test_includes = true` |
| Bộ test mẫu VALID | `.arrow-sample/WordCounter_Arrow2Test.java`, 5 test |
| Chỉ số khối 4 với bộ mẫu | line và branch 100%; mutation đo và ghi lại ở GĐ5 |
