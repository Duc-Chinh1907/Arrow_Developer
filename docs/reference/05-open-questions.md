# 05. Các điểm chờ xác nhận

Mỗi điểm dưới đây là chỗ mà ảnh framework, `CLAUDE.md` và `Adaptive repair.md` (AR) chưa nói rõ, nói khác nhau, hoặc đã gây lỗi ở bản tool cũ. Spec 00–04 viết theo **quyết định đã chốt** (cột "Quyết định"); điểm còn mở viết theo phương án đề xuất.

Khuôn mỗi điểm: **bối cảnh** → **vấn đề** → **ảnh hưởng tới RQ** → **các phương án** → **đề xuất** → **nếu chọn khác thì sửa gì**.

| # | Điểm | Ảnh hưởng | Quyết định (14/10/2026) |
| --- | --- | --- | --- |
| Q1 | Ảnh ghi "Fine-Tuned Open-Source Code Model" | Hình trong báo cáo | **Chốt:** nhãn "Selected LLM (commercial API / open-weight model)", không fine-tune |
| Q2 | Fixed Repair và EvoSuite không có trong ảnh | RQ2, RQ3 | **Chốt A:** đặt trong khối có sẵn |
| Q3 | Bộ test nào đại diện ARROW ở RQ3 | RQ3 | **Chốt A:** mỗi task, mỗi model một bộ (`zero_shot_project_aware` VALID ngay, nếu không thì sau Adaptive); báo riêng từng model, từng dataset |
| Q4 | Prompt sửa có kèm metadata M không | RQ2 | **Chốt C:** M đầy đủ cho mọi prompt sửa (phân tích nhược điểm ở dưới) |
| Q5 | Adaptive kẹt ở mức 3 khi mã trùng | RQ2 | **Chốt B:** mức 3 không tiến triển thì về mức 2; prompt mức 3 có danh sách đã thử |
| Q6 | Quy tắc chọn N | RQ2 | **Chốt A:** quy tắc của CLAUDE.md; chạy pilot sau khi code xong để chọn N |
| Q7 | Prompt mức 2 cho UNKNOWN | RQ2 | **Chốt B:** khối lỗi build đầy đủ và cấu hình build |
| Q8 | Timeout | RQ1, RQ2 | **Chốt:** thử tối đa 3 lần; vẫn timeout thì ghi INFRA và ghi rõ cần chạy lại riêng task đó |

---

## Q1. Nhãn "Fine-Tuned Open-Source Code Model" trong ảnh

**Đang ở đâu:** khối 2 của ảnh, hộp dưới cùng "Selected LLM / Fine-Tuned Open-Source Code Model". Spec 02 Mục 4.3.

**Bối cảnh:** `CLAUDE.md` (Cấu hình thực nghiệm, chốt 07/10/2026) ghi: **không fine-tune cả hai model** (đã bỏ hẳn), để cô lập tác động của ARROW, đúng tinh thần zero-shot. Hai model là GPT-6 Luna (API, `reasoning_effort: none`) và Qwen3-Coder-30B-A3B-Instruct (trọng số mở, tự host qua Ollama, non-thinking), đều gọi qua LiteLLM.

**Vấn đề:** ảnh nói có model fine-tune, phương pháp nói không. Nếu ảnh này vào Report, người đọc sẽ hiểu ARROW có fine-tune.

**Ảnh hưởng:** không ảnh hưởng số liệu; ảnh hưởng tính nhất quán của báo cáo. Hội đồng dễ hỏi "fine-tune trên dữ liệu nào?".

| Phương án | Làm gì | Ưu | Nhược |
| --- | --- | --- | --- |
| **A. Sửa nhãn ảnh** | Đổi thành "Selected LLM (GPT-6 Luna / Qwen3-Coder-30B)" hoặc "Selected LLM (commercial API / open-weight model)" | Khớp phương pháp | Phải sửa ảnh |
| B. Giữ ảnh, ghi chú trong báo cáo | Thêm câu "không fine-tune" | Không sửa ảnh | Ảnh và chữ mâu thuẫn |
| C. Làm fine-tune | Thêm bước fine-tune | Khớp ảnh | Trái quyết định đã chốt, thêm khối lượng lớn |

**Quyết định (14/10/2026): A**, nhãn mới **"Selected LLM (commercial API / open-weight model)"**. Spec không có bước fine-tune; lớp gọi model chỉ định tuyến tới model đã chọn (spec 02 Mục 4).

**Nếu chọn khác:** B: không sửa spec. C: phải thêm spec cho dữ liệu, quy trình fine-tune và phục vụ model; không khuyến nghị.

---

## Q2. Vị trí của Fixed Repair và EvoSuite

**Đang ở đâu:** spec 03 Mục 6.6 (Fixed), Mục 9 (xác minh EvoSuite); spec 04 Mục 3 (sinh EvoSuite).

**Bối cảnh:** ảnh chỉ vẽ con đường của ARROW. Nhưng:
- **RQ2** so Adaptive Repair với **Fixed Repair** (một prompt ở mọi vòng, luôn sửa từ bản mới nhất, không hash/signature/checkpoint; AR Mục 7).
- **RQ3** so bộ test của ARROW với **EvoSuite**. Nhóm đã chốt phương án C: EvoSuite chạy cấu hình mặc định, không quy đổi ngân sách theo ARROW, nhưng bộ test của nó qua **cùng 5 bước xác minh**.

**Vấn đề:** cần đặt Fixed và EvoSuite vào framework sao cho điều kiện so sánh **giống hệt**: cùng workspace, cùng cờ build, cùng JDK, cùng cách xác minh, cùng cách đo.

| Phương án | Làm gì | Ưu | Nhược |
| --- | --- | --- | --- |
| **A. Đặt trong khối có sẵn** | Fixed là một `IRepairPolicy` thứ hai trong khối 3 (dùng chung xác minh, chẩn đoán, vòng lặp, chỉ khác phần quyết định). EvoSuite sinh ở khối 4 (Flow 4.1), bộ test đi qua Flow 3.1 của khối 3, rồi đo ở khối 4 | Điều kiện so sánh giống hệt vì dùng chung code; ít code | Ảnh không thể hiện; cần một câu giải thích trong báo cáo |
| B. Thêm khối 5 "Baselines" | Fixed và EvoSuite nằm trong khối riêng | Ảnh/spec tách bạch | Dễ viết lại phần xác minh, có nguy cơ lệch điều kiện |
| C. Chạy ngoài framework | Script riêng | Framework gọn | Không bảo đảm cùng điều kiện; khó tái lập |

**Quyết định (14/10/2026): A.** Công bằng của RQ2 và RQ3 dựa vào việc dùng chung đúng một bộ xác minh và một bộ đo. Có thể bổ sung vào ảnh một hộp nhỏ "Fixed Repair (baseline)" cạnh "Adaptive Repair Algorithm" và một mũi tên "EvoSuite suites" vào khối 4.

**Nếu chọn khác:** B: tách Mục 6.6 và Mục 9 của spec 03 cùng Mục 3 của spec 04 thành spec 05-baselines, nhưng vẫn gọi lại `IVerifier` và `IQualityTool`.

---

## Q3. Bộ test nào đại diện cho ARROW ở RQ3

**Đang ở đâu:** spec 04 Mục 6.5.

**Bối cảnh:** mỗi task có thể có **nhiều** bộ test VALID: 2 model × 3 quy trình prompting × {VALID ngay khi sinh (`initial`), sửa bằng Fixed, sửa bằng Adaptive}, tức tối đa 18 bộ. EvoSuite có **một** bộ mỗi task. RQ3 hỏi "bộ test hợp lệ **do ARROW sinh**" so với EvoSuite.

**Vấn đề:** không định nghĩa rõ thì có thể (vô tình) chọn bộ tốt nhất cho ARROW, so với một bộ duy nhất của EvoSuite, làm kết quả thiên lệch.

| Phương án | "Bộ test của ARROW" cho mỗi task và mỗi model | Ưu | Nhược |
| --- | --- | --- | --- |
| **A. Quy trình ARROW đầy đủ** | Candidate `zero_shot_project_aware` (Đóng góp 1): nếu VALID ngay thì lấy bộ `initial`; nếu không thì lấy kết quả chain **Adaptive** (Đóng góp 2) nếu VALID. Mỗi task tối đa một bộ mỗi model | Đúng định nghĩa ARROW (cả 3 đóng góp); một bộ đối một bộ như EvoSuite; không chọn lọc theo kết quả | Ít bộ hơn (chỉ những task ARROW đầy đủ ra được VALID) |
| B. Gộp mọi bộ VALID | Mọi quy trình, mọi cơ chế | Nhiều dữ liệu | Trộn cả `zero_shot`, `few_shot`, Fixed (không phải ARROW); một task có nhiều bộ làm lệch trung bình |
| C. Bộ tốt nhất mỗi task | Bộ có coverage hoặc mutation cao nhất | Số đẹp | Chọn lọc theo kết quả (cherry-picking), không công bằng với EvoSuite |

**Cách so:** ghép cặp trên các task **cả ARROW và EvoSuite đều có bộ VALID**; báo thêm số task chỉ một bên có bộ VALID (đây cũng là một kết quả: khả năng tạo được bộ test hợp lệ). Báo riêng từng model (GPT-6 Luna, Qwen3-Coder-30B) và từng dataset.

**Quyết định (14/10/2026):** báo **riêng từng model** (GPT-6 Luna, Qwen3-Coder-30B) **và từng dataset** (Classes2Test content-matched, GitBug-Java upstream-pinned): mỗi tổ hợp model × dataset một bảng ARROW vs EvoSuite.

Bộ đại diện ARROW theo phương án **A** (chốt 14/10/2026); các bộ khác (quy trình khác, Fixed) báo cáo kèm như số liệu bổ sung, không dùng để kết luận RQ3.

**Nếu chọn khác:** B hoặc C: sửa bảng hệ thống và cách ghép cặp ở spec 04 Mục 6.5.

---

## Q4. Prompt sửa có kèm metadata M không

**Đang ở đâu:** spec 02 Mục 2.2, spec 03 Mục 6.5–6.6.

**Bối cảnh:**
- `CLAUDE.md` (Đóng góp 1): thông tin repository "được đưa vào prompt **xuyên suốt các bước sinh, xác minh và sửa**".
- AR Mục 3.1: mức 1 (cũng là prompt của Fixed) = thông báo lỗi + mã test + source CUT, **không** có M; mức 2 COMPILE thêm dependencies và Java version.

**Vấn đề:** hai tài liệu nói khác nhau. Thêm nữa, không có Java version thì model dễ sửa bằng API mới hơn mức Java của dự án (ví dụ `List.of` cho dự án Java 8), tức là lỗi do thiếu thông tin chứ không do cơ chế.

**Ảnh hưởng:** RQ1 đo trên candidate ban đầu (c0) nên không bị ảnh hưởng. RQ2 bị ảnh hưởng: nếu chỉ Adaptive có M thì chênh lệch có thể do thông tin chứ không do cơ chế (AR Mục 3.1 đã lập luận tương tự khi thêm source CUT vào mức 1).

| Phương án | Làm gì | Ưu | Nhược |
| --- | --- | --- | --- |
| A. Không có M trong prompt sửa | Đúng AR | Không sửa AR | Trái `CLAUDE.md`; dễ sửa sai Java version |
| **B. M rút gọn cho mọi prompt sửa** | 4 dòng: build tool, Java version, test framework, test path; cho **cả Fixed và Adaptive**, mọi mức. Danh sách dependencies đầy đủ vẫn chỉ ở mức 2 COMPILE của Adaptive | Khớp `CLAUDE.md`; công bằng hai cơ chế; giữ phần "thông tin bổ sung" của mức 2 là điểm khác biệt | Phải cập nhật AR Mục 3.1 và Mục 7 |
| C. M đầy đủ cho mọi prompt sửa | Kể cả dependencies | Đơn giản | Mức 2 COMPILE mất ý nghĩa; prompt dài hơn |

**Quyết định (14/10/2026): C**, M đầy đủ cho mọi prompt sửa (Fixed và Adaptive, mọi mức, mọi quy trình sinh). Phân tích hai nhược điểm của C:

**Nhược điểm 1: "Mức 2 COMPILE mất ý nghĩa", thực ra chỉ mất một phần nhỏ.** Theo AR, thông tin bổ sung của mức 2 COMPILE gồm ba thứ:

| Thông tin mức 2 COMPILE (AR) | Với C (M đầy đủ ở mọi mức) | Còn là điểm khác của mức 2? |
| --- | --- | --- |
| (a) API thật của CUT và các class liên quan: constructor, method, field thật | M **không** có (M chỉ có cấu hình repository, không có API của class) | **Có**, và đây là phần quan trọng nhất để sửa `cannot find symbol`, `constructor cannot be applied` |
| (b) Danh sách dependencies | Đã có trong M ở mọi mức | Không (trùng) |
| (c) Java version | Đã có trong M ở mọi mức | Không (trùng) |

Vậy mức 2 COMPILE vẫn khác mức 1 ở (a) và ở danh sách "đã thử và thất bại". Spec 03 Mục 6.5 đã chỉnh nội dung mức 2 COMPILE: API thật (chữ ký) của CUT và **mọi class xuất hiện trong lỗi** (kể cả class của thư viện, đọc từ jar đã phân giải), và với lỗi `package does not exist` thì liệt kê package thật có trong các jar test. Phần này M không có, nên mức 2 vẫn có ý nghĩa rõ ràng.

**Nhược điểm 2: "Prompt dài hơn", tăng nhưng không đáng kể.**
- Khối M đầy đủ: 7 dòng cố định + tối đa 25 dòng dependencies ≈ 300–600 token.
- Prompt sửa ở pilot cũ (shard-01): 428.436 token vào / 134 lượt ≈ 3.200 token mỗi lượt. Thêm M làm tăng khoảng 10–20%.
- Chi phí thêm: 134 lượt × 500 token × 0,05 USD / 1 triệu token ≈ **0,003 USD** mỗi shard. Thời gian gọi model gần như không đổi.
- Qwen qua Ollama: cần cửa sổ ngữ cảnh (`num_ctx`) đủ cho prompt + output; thêm 500 token không thay đổi yêu cầu này (vẫn phải kiểm tra riêng khi cài Qwen).

**Lợi của C:** khớp đúng `CLAUDE.md` ("thông tin repository đưa vào xuyên suốt sinh, xác minh, sửa"); một khối M duy nhất cho mọi prompt, đơn giản; Fixed cũng biết Java version nên không bị thua vì thiếu thông tin cấu hình, nên khác biệt RQ2 chỉ còn do cơ chế (thông tin theo loại lỗi, checkpoint, rollback, mã trùng).

**Cách viết trong báo cáo:** "Cả hai cơ chế nhận cùng thông tin cấu hình repository trong mọi prompt sửa; Adaptive khác ở thông tin bổ sung theo loại lỗi, việc giữ checkpoint và phát hiện mã trùng."

**Việc kéo theo:** sửa AR Mục 3.1 (mức 1 và mức 2 COMPILE: bỏ dependencies, Java version khỏi phần bổ sung vì đã có ở mọi mức) và Mục 7 (prompt Fixed thêm khối cấu hình repository).

---

## Q5. Adaptive kẹt ở mức 3 khi mã trùng liên tục

**Đang ở đâu:** spec 03 Mục 6.4 (`NextLevel`), Mục 6.5 (nội dung mức 3).

**Bối cảnh (AR Mục 3.1, 4):**
- Mã trùng hoặc output không hợp lệ → tính một vòng, không build, `level ← min(level + 1, 3)`.
- Vòng sau luôn sửa từ checkpoint; prompt theo lỗi của checkpoint.
- Prompt mức 3 = chỉ viết lại method lỗi; **không** có danh sách "đã thử và thất bại" (chỉ mức 2 có).

**Vấn đề (đã xảy ra ở pilot cũ):** khi đã ở mức 3 mà model trả lại mã trùng:
1. Mã trùng không được build, nên không có so sánh, checkpoint không đổi, không rollback.
2. Mức đã là 3, `min(3 + 1, 3) = 3`, nên không đổi prompt.
3. Checkpoint, mức và nội dung prompt giống hệt vòng trước, nên request giống hệt từng chữ. Với `temperature = 0`, model gần như chắc chắn trả lại cùng câu trả lời. (Ở tool cũ còn tệ hơn: cache dùng chung theo nội dung nên model không được hỏi lại lần nào. Framework mới đã bỏ kiểu cache này, spec 02 Mục 4.4: mỗi vòng là một lượt gọi riêng; nhưng cùng prompt + temperature 0 thì vẫn ra cùng kết quả, nên vấn đề vẫn còn.)
4. Lại mã trùng → quay lại bước 1, tới hết N.

Ví dụ thật ở shard-01 (Mac): CUT `67934497`, `few_shot`, Adaptive: vòng 1–2 COMPILE, vòng 3 lên mức 3 và trả mã trùng, vòng 4–10 đều "mức 3, mã trùng", lấy từ cache, model không được hỏi lại. Shard-01 có 5 chain Adaptive kẹt kiểu này.

**Giải thích dễ hiểu: chuyện gì đang xảy ra.** Hình dung mỗi vòng sửa, framework gửi cho model **một câu hỏi** gồm 4 phần: (1) mã test đang sửa (checkpoint), (2) lỗi của mã đó, (3) chỉ dẫn theo mức, (4) danh sách "đã thử" (chỉ mức 2 có). Câu hỏi chỉ thay đổi khi một trong 4 phần thay đổi.

| Vòng | Câu hỏi gửi đi | Model trả lời | Framework quyết định | Phần nào của câu hỏi vòng sau thay đổi? |
| --- | --- | --- | --- | --- |
| 1 | Mức 1: checkpoint c0 + lỗi COMPILE "cannot find symbol" | Bản A, vẫn COMPILE cùng lỗi | Lỗi lặp → lên mức 2 | Mức (1 → 2) |
| 2 | Mức 2: c0 + lỗi + API thật + đã thử [vòng 1] | Bản B, vẫn COMPILE cùng lỗi | Lỗi lặp → lên mức 3 | Mức (2 → 3) |
| 3 | Mức 3: c0 + lỗi + "chỉ viết lại method `testFoo`" | Bản giống hệt bản A (đã thử) | **Mã trùng** → không build; lên mức: `min(3+1, 3)` = **3** | **Không phần nào**: checkpoint vẫn c0 (không có bản nào được build để thay), lỗi vẫn của c0, mức vẫn 3, mức 3 không có danh sách đã thử |
| 4 | **Giống hệt vòng 3, từng chữ** | Model nhận đúng câu hỏi cũ, temperature 0 → trả lại đúng câu trả lời cũ (tool cũ: lấy luôn từ cache, không gọi model) | Mã trùng → mức vẫn 3 | Không phần nào |
| 5–10 | Giống hệt vòng 3 | Như vòng 4 | Mã trùng → mức vẫn 3 | Không phần nào |

Nói ngắn: từ vòng 3, framework **hỏi đi hỏi lại đúng một câu**, và vì câu hỏi giống hệt (temperature 0) nên nhận lại đúng một câu trả lời. Không có lối thoát vì:
- **không rollback**: rollback chỉ xảy ra khi có bản mới được build và tệ hơn; mã trùng thì không build;
- **không đổi checkpoint**: checkpoint chỉ đổi khi có bản mới được build và tốt hơn;
- **không đổi mức**: đã ở mức cao nhất;
- **không đổi danh sách đã thử trong câu hỏi**: mức 3 không gửi danh sách này.

Vì vậy chain "đứng yên" 7 vòng mà không thử được gì mới. Với phương án B, vòng 4 quay về mức 2 và câu hỏi có thêm dòng "vòng 3: mã trùng" trong danh sách đã thử, nên câu hỏi khác mọi câu đã hỏi → model có cơ hội trả lời khác.

**Ảnh hưởng tới RQ2:**
- Adaptive mất các vòng cuối mà không thử được gì mới, nên tỷ lệ thành công có thể thấp hơn khả năng thật.
- "Số vòng sửa" của chain thất bại vẫn tính N nên không đổi; nhưng log cho thấy cơ chế "đứng yên" nhiều vòng, khó bảo vệ trước câu hỏi "vì sao Adaptive lặp 7 lần".
- Mâu thuẫn với tinh thần "nhận ra khi không tiến triển và đổi cách sửa".

| Phương án | Làm gì | Ưu | Nhược |
| --- | --- | --- | --- |
| A. Giữ AR | Ghi nhận trong báo cáo là "không tiến triển", phân tích P xử lý | Không sửa AR | Adaptive tự làm phí vòng; khó bảo vệ |
| **B. Xoay mức 3 → 2, và mức 3 có danh sách đã thử** | `NextLevel(3) = 2`: đã ở mức cao nhất mà vẫn trùng/không hợp lệ/tệ hơn/lặp thì quay về mức 2 (thông tin bổ sung + đã thử), lần lặp sau lại lên 3. Prompt mức 3 thêm danh sách đã thử | Request mỗi vòng khác nhau (danh sách đã thử dài thêm) nên model được hỏi lại thật; giữ đúng ý "đưa cách đã thử và thất bại"; không đổi tham số model | Sửa AR; mức không còn chỉ đi lên |
| C. Chỉ thêm danh sách đã thử vào mức 3 | Giữ ở mức 3 | Thay đổi nhỏ nhất | Model có thể vẫn trả lại cùng method dù prompt khác |
| D. Dừng chain khi trùng ở mức 3 | Coi là stagnation và dừng | Tiết kiệm | Trái quyết định "chạy hết N, P tính lại từ log" |
| E. Tăng temperature cho request lặp | Ép model trả lời khác | Đơn giản | Đổi tham số giữa các vòng, trái nguyên tắc tham số cố định; mất tái lập |

**Phương án B gồm hai thay đổi nhỏ:**

1. **Xoay mức 3 → 2.** Khi chain **đang ở mức 3** mà vòng đó không tiến triển (mã trùng, output không hợp lệ, bản mới tệ hơn, hoặc lỗi lặp cùng signature), mức tiếp theo là **2** thay vì đứng yên ở 3. Ở mức 2 mà lại không tiến triển thì lên 3, rồi lại về 2… Mức đi theo vòng `1 → 2 → 3 → 2 → 3 → …` cho tới khi có bản tốt hơn (về mức 1 nếu đổi loại lỗi) hoặc hết N.
2. **Mức 3 có danh sách đã thử.** Prompt mức 3 ngoài "chỉ viết lại method lỗi" còn kèm danh sách "đã thử và thất bại", giống mức 2.

Hai thay đổi này bảo đảm **câu hỏi vòng sau luôn khác vòng trước**, vì mỗi vòng không tiến triển đều thêm một dòng vào danh sách đã thử, và mức cũng đổi. Câu hỏi khác thì không trúng cache, model được hỏi thật.

**Ví dụ so sánh A (AR hiện tại) và B**, cùng một chain Adaptive. CUT `OrderService`; c0 lỗi biên dịch trong method `totalPriceAddsTax`: `cannot find symbol: method getTotal(long)`, signature `COMPILE | cannot find symbol | method`. N = 10.

| Vòng | Theo A (AR hiện tại) | Theo B |
| --- | --- | --- |
| 1 | Mức 1 (lỗi + mã + CUT + M). Model trả bản r1, vẫn gọi `getTotal` → COMPILE, **cùng signature** → lỗi lặp → đã thử = [r1]; lên **mức 2** | Giống A |
| 2 | Mức 2 (+ API thật của `OrderService` + đã thử [r1]). Model trả r2, gọi `calcTotal` (cũng không tồn tại) → COMPILE cùng signature → đã thử = [r1, r2]; lên **mức 3** | Giống A |
| 3 | Mức 3: "chỉ viết lại method `totalPriceAddsTax`" (không có đã thử). Model viết lại method giống hệt r1 → ghép vào checkpoint → **mã trùng r1** → không build; đã thử = [r1, r2, r3 trùng]; `min(3+1,3)` = **mức 3** | Mức 3: "chỉ viết lại `totalPriceAddsTax`" **+ đã thử [r1, r2]**. Giả sử model vẫn trả method giống r1 → **mã trùng** → không build; đã thử = [r1, r2, r3 trùng]; xoay về **mức 2** |
| 4 | Prompt mức 3 **giống hệt vòng 3** (checkpoint c0, cùng lỗi, cùng method, không có đã thử) → temperature 0 → model trả lại câu trả lời cũ → mã trùng → mức 3 | Prompt mức 2: API thật + đã thử **[r1, r2, r3 trùng]**. Khác prompt vòng 2 (thêm dòng r3) → **model nhận câu hỏi mới**. Model thấy cả `getTotal` và `calcTotal` đều đã sai, dùng đúng `totalPrice(long)` trong danh sách API → biên dịch được; 1 assertion sai → **ASSERTION**: tốt hơn checkpoint, đổi loại lỗi → checkpoint = r4, về **mức 1** |
| 5 | Như vòng 4 | Mức 1 cho ASSERTION (method fail + expected/actual). Model sửa giá trị expected → 5/5 pass, module không regression → **VALID** |
| 6–10 | Như vòng 4: cùng câu hỏi, cùng câu trả lời, mã trùng | (đã xong ở vòng 5) |
| Kết quả | **Thất bại** sau 10 vòng; 7 vòng cuối không hỏi model lần nào | **Thành công ở vòng 5** |

Prompt mức 3 theo B (vòng 3) trông như sau:

```
The test class below does not compile. Rewrite ONLY the method totalPriceAddsTax;
keep every other method exactly as it is.
Repository configuration: Maven 3.9.6 · Java 8 · JUnit 4 (4.13.2) · Mockito 3.12.4 · … (khối M đầy đủ)
Errors:
  OrderService_ArrowTest.java:[14,30] cannot find symbol  symbol: method getTotal(long)
Additional information for this failure:                 ← mức 3 giữ thông tin bổ sung của mức 2 (chốt 14/10/2026)
  Constructors, methods and fields of com.acme.shop.OrderService: … public long totalPrice(long orderId) …
Approaches that already failed (do not repeat them):        ← phần B thêm vào mức 3
- Round 1 · COMPILE | cannot find symbol | method
  First error: cannot find symbol: method getTotal(long) in class com.acme.shop.OrderService
  Changed code: …
- Round 2 · COMPILE | cannot find symbol | method
  First error: cannot find symbol: method calcTotal(long) in class com.acme.shop.OrderService
  Changed code: …
Test class: ```java … ```
Class under test: ```java … ```
```

**Nếu model vẫn cứng đầu:** theo B, vòng 4 có thể lại trùng; khi đó đã thử = [r1, r2, r3 trùng, r4 trùng], lên mức 3; vòng 5 prompt mức 3 có 4 dòng đã thử (khác vòng 3), lại hỏi model thật… Mỗi vòng vẫn là một câu hỏi mới, không còn cảnh "hỏi lại đúng một câu 7 lần". Nếu hết N mà không thành công thì chain thất bại như bình thường; số vòng mã trùng được đếm trong báo cáo.

**Trường hợp mức 3 không áp dụng** (checkpoint ở DISCOVERY, UNKNOWN, REGRESSION, hoặc không xác định được method lỗi): vòng đó vốn đã dùng prompt mức 2; với B, danh sách đã thử vẫn dài thêm mỗi vòng nên câu hỏi vẫn khác nhau.

**Quyết định (14/10/2026): B.** Ví dụ ngắn theo B: vòng 3 mức 3 trùng → `attempts` thêm "round 3: duplicate", mức 2; vòng 4 prompt mức 2 có thêm dòng đó (request khác vòng 2) → model trả lời mới.

**Nếu chọn khác:** A: `NextLevel(3) = 3`, mức 3 không có danh sách đã thử. C: `NextLevel(3) = 3`, mức 3 có danh sách đã thử. D: thêm `stop_reason = stagnation` khi trùng ở mức 3.

**Việc kéo theo (nếu B hoặc C):** sửa AR Mục 3.1, 3.3 và giả mã Mục 4; câu trả lời hội đồng "Strategy switching đổi khi nào?" (AR Mục 11) thêm ý "hết mức thì quay lại bổ sung thông tin".

---

## Q6. Quy tắc chọn N

**Đang ở đâu:** spec 00 Mục 3.1 (`repair.n`), spec 04 Mục 6.3.

**Bối cảnh:**
- `CLAUDE.md` (Adaptive Repair, hạng mục 2, chốt 09/10/2026): N mặc định = 5. Pilot phần 2 chạy hai cơ chế tới 10 vòng trên ứng viên thất bại của Classes2Test; **N = vòng k nhỏ nhất (5 ≤ k ≤ 10) mà cả vòng k−1 và vòng k không có thêm ứng viên sửa thành công**, trên tỷ lệ cộng dồn chung của hai cơ chế; không có k nào thỏa thì N = 10.
- AR Mục 6.1 và định nghĩa RQ2 viết "tối đa N = 5 vòng".
- Tool cũ dùng quy tắc `share95` (vòng đạt 95% số thành công của vòng 10) và ra N = 4. Quy tắc này **không** phải quy tắc đã chốt, và N = 4 nằm dưới mức tối thiểu 5.

**Ví dụ với số liệu shard-01 cũ:** S(k) cộng dồn = 11, 18, 22, 25, 26, 26, 26, 26, 26, 26.
- k = 5: vòng 4 có thêm (25 > 22) → không.
- k = 6: vòng 5 có thêm (26 > 25) → không.
- k = 7: vòng 6 (26 = 26) và vòng 7 (26 = 26) không có thêm → **N = 7**.

**Vấn đề:** cần chốt quy tắc, và chốt cách viết RQ2 khi N khác 5.

| Phương án | Quy tắc | Ưu | Nhược |
| --- | --- | --- | --- |
| **A. Quy tắc CLAUDE.md** | Như trên | Đã chốt; có cận dưới 5 khớp tài liệu (ChatUniTest 5, TestART 4) | N có thể > 5, phải sửa chữ "N = 5" trong RQ2/AR |
| B. Cố định N = 5, không chọn bằng pilot | | Đơn giản, khớp RQ2 hiện tại | Bỏ phần 2 của pilot; AR tự thừa nhận ChatUniTest không chứng minh 5 |
| C. `share95` | | | Không phải quy tắc đã chốt; có thể ra < 5 |

**Quyết định (14/10/2026): A.** Sau khi code xong ARROW Framework, chạy pilot với `n = 10` để chọn N theo quy tắc trên; thực nghiệm chính dùng N đã chọn cho cả Fixed và Adaptive.

**Việc kéo theo:** nếu pilot ra N ≠ 5, sửa định nghĩa "sửa thành công" của RQ2 trong `CLAUDE.md`, AR Mục 6.1 và Mục 8 thành "trong tối đa N vòng (N chọn bằng pilot)".

---

## Q7. Prompt mức 2 cho trạng thái UNKNOWN

**Đang ở đâu:** spec 03 Mục 6.5.

**Bối cảnh:** UNKNOWN = build thất bại nhưng log không có lỗi javac nào trong file test sinh ra, ví dụ plugin lỗi, annotation processor lỗi, task Gradle lỗi. AR thêm UNKNOWN ngày 09/10/2026 (xếp sau COMPILE) và nói mức 3 không áp dụng cho UNKNOWN (dùng mức 2). Nhưng bảng "thông tin bổ sung ở mức 2" của AR **không có dòng UNKNOWN**.

**Vấn đề:** khi một chain đang ở UNKNOWN và lên mức 2, prompt mức 2 phải có gì?

**Lưu ý:** ở tool cũ, phần lớn UNKNOWN trên Windows thực ra là lỗi biên dịch bị đọc sai đường dẫn `/D:/…` (spec 03 Mục 3.2 đã sửa). Sau khi sửa, UNKNOWN thật sẽ hiếm (shard-01 trên Mac có 0 trường hợp ở bước sinh).

| Phương án | Mức 2 của UNKNOWN | Ưu | Nhược |
| --- | --- | --- | --- |
| A. Không có thông tin bổ sung | Mức 1 + danh sách đã thử | Không thêm gì vào AR | Mức 2 gần như giống mức 1, leo mức ít tác dụng |
| **B. Khối lỗi build đầy đủ + cấu hình build** | Toàn bộ khối lỗi build (tối đa 120 dòng, mức 1 chỉ 60) và cấu hình build trong M (build tool, plugin test, Java version) | Đúng nguyên tắc "thông tin bổ sung đúng loại lỗi" | Thêm một dòng vào bảng của AR |
| C. Xử lý như COMPILE | Dùng thông tin bổ sung của COMPILE | Đơn giản | Không đúng bản chất: không có symbol lỗi để tra API |

**Quyết định (14/10/2026): B.**

**Việc kéo theo:** thêm dòng UNKNOWN vào bảng "Thông tin bổ sung ở mức 2" của AR Mục 3.1.

---

## Q8. Timeout (bước build hoặc test chạy quá thời gian)

**Đang ở đâu:** spec 03 Mục 3.5; spec 01 Mục 6; spec 04 Mục 5, 6.6; spec 00 Mục 5.3.

**Bối cảnh:** AR Mục 2.1 (cập nhật 09/10/2026): "Build timeout và lỗi công cụ là lỗi hạ tầng: build lại 1 lần, vẫn lỗi thì dừng ứng viên và loại khỏi RQ2." Timeout có thể do máy (máy chậm, ngủ, mạng chậm khi tải dependency) hoặc do chính test sinh ra (vòng lặp vô hạn, chờ socket).

**Quyết định (14/10/2026):** timeout là lỗi hạ tầng, **gọi lại** chứ không tính cho test:
1. Bước bị timeout (biên dịch, chạy riêng test sinh ra, chạy cả module, baseline, đo chất lượng) được chạy lại, **tối đa 3 lần thử** (`verification.infra_attempts = 3`), chờ 30 s giữa hai lần.
2. Vẫn timeout ở lần thử thứ 3 → job ghi `INFRA` (`B3_TIMEOUT`, `B1_BASELINE_TIMEOUT`…) với `needs_rerun = true`, kèm bước nào, mấy lần, mỗi lần bao lâu.
3. Báo cáo có danh sách **"Cần chạy lại riêng"** (`reports/needs_rerun.csv`), mỗi dòng kèm lệnh chạy lại riêng task đó: `arrow run --run-id <run> --tasks <task_id> --rerun-infra`. Lệnh chỉ chạy lại các job INFRA của task (chain sửa thì từ đầu chain; lượt gọi model đã có trong cache không tốn tiền).
4. Job INFRA **chưa có kết quả**: chưa tính vào RQ1/RQ2/RQ3 cho tới khi chạy lại xong. Run chỉ hoàn chỉnh khi danh sách này rỗng.
5. Các lỗi hạ tầng khác (không tải được dependency, công cụ chết, hết đĩa) xử lý giống hệt: 3 lần thử rồi vào danh sách cần chạy lại.

**Lưu ý (để nhóm theo dõi, không đổi quyết định):** nếu chính test sinh ra tự treo (ví dụ vòng lặp vô hạn), chạy lại riêng vẫn sẽ timeout. Khi đó báo cáo đánh dấu `timeout_repeated = true` cho job đó sau lần chạy lại riêng, để nhóm xem log và quyết định (ví dụ chấp nhận tính là RUNTIME cho riêng trường hợp đó). Framework không tự đổi trạng thái.

**Việc kéo theo:** sửa AR Mục 2.1 ("build lại 1 lần" → "chạy lại tối đa 3 lần thử, vẫn lỗi thì ghi cần chạy lại riêng task") và Mục 13 (lịch sử thay đổi).
