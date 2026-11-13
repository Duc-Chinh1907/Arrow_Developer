Fixture cho xác minh 5 bước của khối 3: CUT `PriceCalculator`; test sẵn có `TotalsTest` phụ thuộc biến static
`Invoice.defaultRate` (candidate đổi biến này mà không đặt lại → REGRESSION). Surefire chạy class theo thứ tự chữ cái
để `TotalsTest` luôn chạy sau `PriceCalculator_ArrowTest`.

Bộ test mẫu VALID (thêm 14/11/2026, GĐ5): `.arrow-sample/PriceCalculator_ArrowTest.java`, 4 test. Khối 4 đo được line, branch và method 100%, không có mutant `NO_COVERAGE`, mutation ≥ 50%.
