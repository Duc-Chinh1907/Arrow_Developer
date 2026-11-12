Fixture cho xác minh 5 bước của khối 3: CUT `PriceCalculator`; test sẵn có `TotalsTest` phụ thuộc biến static
`Invoice.defaultRate` (candidate đổi biến này mà không đặt lại → REGRESSION). Surefire chạy class theo thứ tự chữ cái
để `TotalsTest` luôn chạy sau `PriceCalculator_ArrowTest`.
