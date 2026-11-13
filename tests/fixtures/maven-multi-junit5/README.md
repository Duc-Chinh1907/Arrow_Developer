Fixture: reactor hai module (`shop-core` phụ thuộc `shop-api`, JUnit 5), và một project mẫu `samples/standalone`
không thuộc reactor gốc (lệnh `-pl samples/standalone -am` từ gốc thất bại, build riêng trong thư mục thành công; giống mẫu 9580168).

Bộ test mẫu VALID (thêm 14/11/2026, GĐ5): `.arrow-sample/OrderService_ArrowTest.java` (module `shop-core`), 3 test. Khối 4 đo được line và branch 100%, mutation ≥ 50%; PIT dùng `junit-platform-launcher` 1.10.2, đúng phiên bản JUnit Platform của module.
