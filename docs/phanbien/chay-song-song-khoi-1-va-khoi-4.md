# Phản biện: chạy song song ở khối 1 và khối 4

Ngày: 16/11/2026. Trạng thái: **đã quyết** (mục 8): không làm chế độ song song; làm các cách ở mục 5.

## 1. Đề xuất

Thêm cấu hình chọn cách chạy:

- **Khối 1, hai lượt baseline:**
  - chạy lần lượt (như hiện tại);
  - chạy song song trên hai bản sao riêng của cùng snapshot, rồi gom kết quả để phân loại pass / fail / flaky.
- **Khối 4, JaCoCo, PIT và tsDetect:**
  - cả ba chạy lần lượt (như hiện tại);
  - cả ba chạy song song, mỗi công cụ một luồng;
  - hai nhánh song song: tsDetect, và JaCoCo → PIT.

## 2. Tóm tắt đánh giá

- Cả hai chỗ đều chạy song song được về kỹ thuật: mỗi lượt baseline và mỗi công cụ đo đã chạy trên một bản sao riêng của project.
- **Thời gian tiết kiệm được khiêm tốn**: ở khối 1 là thời gian của một lượt baseline (mỗi module một lần); ở khối 4 khoảng 8–30 s mỗi class.
- **Rủi ro sai kết quả là có thật**: ở khối 1 là phân loại test flaky sai; ở khối 4 là điểm mutation bị đẩy lên cao giả.
- **Khuyến nghị**: giữ mặc định tuần tự; làm trước các cách tăng tốc không đổi kết quả (mục 5); nếu vẫn làm song song thì chỉ là tuỳ chọn tự bật, có cảnh báo.

## 3. Khối 1: hai lượt baseline song song

### 3.1 Lợi ích

Tiết kiệm đúng thời gian của một lượt baseline, mỗi module một lần (số đo thật):

| Repo | Hai lượt baseline | Tiết kiệm nếu song song |
|---|---|---|
| Teradata/jaqy (`inspect`, 15/11/2026) | 12,3 s + 8,0 s | khoảng 8 s |
| otros-systems/otroslogviewer (GĐ7) | 185,9 s + 188,4 s | khoảng 3 phút |

Với project nhỏ thì không đáng kể; với project có bộ test lớn thì tiết kiệm vài phút.

### 3.2 Rủi ro

1. **Sai mục đích của hai lượt.** Hai lượt baseline dùng để phát hiện test chập chờn (flaky) trong **điều kiện bình thường**. Khi chạy song song, hai lượt tranh CPU, RAM, và các tài nguyên cố định mà test hay dùng: cổng mạng (ví dụ server test mở cổng 8080), file tạm, thư mục trong home. Một test có thể fail **vì chạy chung**, không phải vì nó flaky. Hệ quả cho khối 3:
   - test bị xếp nhầm là **flaky** thì bị loại khỏi phép kiểm regression, nên ARROW có thể bỏ sót việc test sinh ra làm hỏng test sẵn có;
   - test bị xếp nhầm là **fail sẵn** (`failing_in_all_runs`) thì không bao giờ được kiểm.
2. **Điều kiện không khớp với bước 5 của khối 3:** baseline đo dưới tải nặng, còn bước kiểm regression chạy module một mình, nên phép so sánh không còn công bằng.
3. **Gấp đôi bộ nhớ:** hai JVM Maven/Gradle và Surefire cùng lúc; với `--jobs 2` là bốn.
4. **Thuộc spec thuật toán của nhóm (R01).** Theo thứ tự ưu tiên của dự án, R-spec thắng ở phần thuật toán, nên đổi cách chạy baseline cần nhóm đồng ý và ghi "khác R" trong P01.

## 4. Khối 4: JaCoCo, PIT, tsDetect

### 4.1 Số đo thật

| Class | JaCoCo | PIT | tsDetect |
|---|---|---|---|
| `PriceCalculator` (máy sạch, 15/11/2026) | 13,3 s | 8,4 s | 0,4 s |
| `DynamicEntityGraph` (GĐ7) | 24,3 s | 16,5 s | 0,6 s |
| `AnnotationValues` (GĐ7) | 29,2 s | 30,6 s | 1,0 s |
| `Optionalish` (GĐ7) | 44,6 s | 27,1 s | 0,8 s |

### 4.2 Ba chế độ

| Chế độ | Tiết kiệm mỗi class | Nhận xét |
|---|---|---|
| Lần lượt (hiện tại) | 0 | an toàn |
| Cả ba song song | khoảng 8–30 s (khoảng 25–40% thời gian khối 4) | **Rủi ro:** PIT có giới hạn thời gian cho mỗi mutant. Khi máy tải nặng, nhiều mutant bị quá giờ (`TIMED_OUT`), và PIT tính mutant quá giờ là **bị diệt**, nên điểm mutation bị đẩy lên cao giả. Thêm nữa, hai Maven chạy cùng lúc trên cùng `~/.m2`. |
| tsDetect ∥ (JaCoCo → PIT) | khoảng 0,5–1 s (thời gian của tsDetect) | an toàn, nhưng gần như **không nhanh hơn**, vì tsDetect vốn chạy dưới 1 giây |

Mỗi công cụ đã chạy trên bản sao riêng (`work/<run>/q-<tool>-…`, `ts-…`), nên phần kỹ thuật không khó. Vấn đề nằm ở độ tin cậy của số đo.

## 5. Các cách tăng tốc không đổi kết quả

Nên làm trước:

1. **Lưu baseline giữa các lần chạy.** Nếu project chưa đổi (cùng `source_tree_hash` hoặc commit, cùng JDK, cùng lệnh build) thì dùng lại baseline cũ. Ví dụ chạy `arrow inspect` rồi `arrow gen` trên cùng project: hiện baseline chạy lại từ đầu; với cache, lần thứ hai gần như bỏ qua toàn bộ baseline. Đây là cách tiết kiệm nhiều nhất cho người dùng hay chạy lại.
2. **PIT dùng lại bản build của JaCoCo** thay vì tự biên dịch lại. Đây mới là lý do hợp lý cho thứ tự "JaCoCo → PIT" trong chế độ thứ ba.
3. **Bỏ PIT khi JaCoCo cho line 0%**, vì khi đó mutation chắc chắn bằng 0.
4. **`arrow gen --no-pit`** (đã có từ 15/11/2026) cho người cần nhanh.

## 6. Khuyến nghị

1. Làm trước **cache baseline** và **JaCoCo → PIT dùng lại build** (kèm bỏ PIT khi line 0%): nhanh hơn rõ mà không đổi kết quả.
2. **Khối 4, cả ba song song:** chỉ làm như tuỳ chọn tự bật (ví dụ `quality.parallel = true`), mặc định tắt, có cảnh báo về điểm mutation.
3. **Khối 1, baseline song song:** chỉ làm nếu nhóm đồng ý đổi R01; cũng là tuỳ chọn tự bật, mặc định tắt.
4. Nếu làm thêm chế độ, mỗi chế độ cần test riêng (unit và tích hợp), và cần ghi chế độ đã dùng vào `run.json`, để kết quả của các run so sánh được với nhau.

## 7. Cần quyết

- Có làm các chế độ song song không, hay chỉ làm các cách ở mục 5?
- Nếu làm baseline song song: nhóm có đồng ý ghi "khác R01" không?

## 8. Quyết định và triển khai (16/11/2026)

**Quyết định (Chính):** không làm chế độ chạy song song ở khối 1 và khối 4; làm ba cách ở mục 5, theo thứ tự: bỏ PIT khi line 0%, biên dịch một lần cho JaCoCo và PIT, cache baseline. R01 không đổi.

### 8.1 Số đo làm căn cứ: khối 4 biên dịch hai lần

Log khối 4 của các run GĐ7 (giây):

| Class | JaCoCo: biên dịch | JaCoCo: chạy test | PIT: biên dịch lại | PIT: chạy |
|---|---:|---:|---:|---:|
| `ArgumentParser` | 10,7 | 8,1 | 8,4 | 8,6 |
| `DynamicEntityGraph` | 10,9 | 12,7 | 12,4 | 3,8 |
| `Optionalish` | 25,2 | 18,5 | 23,0 | 3,1 |
| `TorrentManager` | 16,0 | 10,9 | 18,0 | 2,3 |
| `AnnotationValues` | 12,4 | 14,3 | 17,2 | 12,2 |

PIT tự biên dịch lại mất 8–23 s, thường lâu hơn chính PIT; biên dịch một lần cho cả hai cắt khoảng 30–45% thời gian khối 4.

### 8.2 Ghi chú triển khai

- **Biên dịch một lần:** an toàn vì JaCoCo gắn agent lúc chạy (không sửa `.class`). Chỉ dùng chung khi bước biên dịch thành công; JaCoCo xoá file `.exec` cũ trước mỗi lần chạy (agent ghi `append=true`).
- **Bỏ PIT:** chỉ khi line **đúng 0%** của chính CUT; JaCoCo lỗi (không biết coverage) thì vẫn chạy PIT. Ghi "PIT bỏ qua vì line 0%", không ghi "mutation 0%".
- **Cache baseline:** khoá gồm `source_tree_hash` (hoặc commit), build root, bộ chọn module, build tool và phiên bản, JDK, classpath đã phân giải, `baseline_runs`, phiên bản ARROW; chỉ lưu `baseline.json`, không lưu bản biên dịch; in rõ khi dùng lại; cờ `--fresh-baseline`, khoá `verification.baseline_cache`; hết hạn sau 7 ngày; ghi nguồn gốc vào `baseline.json`; ghi file nguyên tử. Ghi "khác R01" trong P01, vì R01 đo baseline mới mỗi lần chạy.

### 8.3 Kết quả

| Việc | Trạng thái | Đo |
|---|---|---|
| Bỏ PIT khi line 0% | xong | — |
| Biên dịch một lần cho JaCoCo và PIT | xong | `maven-verify-junit4`: PIT 4,0 s → 1,5 s (hai lần đo), số đo không đổi (line 67%, mutation 67%, 4/6) |
| Cache baseline | xong | `maven-verify-junit4` qua `arrow inspect`: lần hai "baseline dùng lại" thay cho 2 lượt (2,8 s + 2,7 s); với otroslogviewer tiết kiệm khoảng 6 phút mỗi lần chạy lại |

