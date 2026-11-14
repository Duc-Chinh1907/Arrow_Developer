# Chạy thử trên repo thật (GĐ7)

Bản nháp, cập nhật 14/11/2026. Nhóm cần xem và đồng ý bảng này (PLAN GĐ7, "xong khi").

## 1. Bộ repo

- Danh sách: [`validation-set.json`](validation-set.json), rút bằng [`tools/validation/sample_validation_set.py`](../tools/validation/sample_validation_set.py), seed `20261114`.
- Nguồn: `technical_eligible_manifest.jsonl` của 5 shard. `strict_eligible_manifest.jsonl` rỗng ở mọi shard (chưa có revision map được audit), nên Chính chọn dùng bản technical (14/11/2026). Sha256 của từng file nguồn có trong `validation-set.json`.
- Kết quả rút: 20 repo, 23 class, từ 3.336 repo có sẵn. Phần lớn repo chỉ có 1 class trong manifest, nên chưa đạt "2–3 class mỗi repo" của PLAN.

## 2. Cách chạy

```
arrow gen <FQN…> --repo <url> --ref <checkout_sha> --plain --yes --max-tokens 200000
```

- Model: `gpt-6-luna` qua TeamoRouter, `temperature=0`; repair adaptive N = 5.
- Đủ 4 khối (có đo JaCoCo, PIT, tsDetect). Project từ GitHub nên test chỉ lưu trong `runs/`.
- Máy: Windows 11, Maven 3.9.16, JDK 8/11/17/21.

## 3. Đợt thử (2 repo, 14/11/2026)

Hai repo đầu tiên của danh sách có từ 2 class trở lên: #7 và #9.

| Repo | Class | Kết quả | Vòng | Line | Branch | Mutation | Smell | Token | Thời gian |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|
| Teradata/jaqy | `ArgumentParser` | VALID ở c0 | 0 | 97% | 96% | 87% | 2.00 | 3,6k | 182 s |
| Teradata/jaqy | `PathUtils` | VALID ở c0 | 0 | 77% | 52% | 44% | 2.75 | 4,0k | 102 s |
| Cosium/spring-data-jpa-entity-graph | `DynamicEntityGraph` | VALID ở c0 | 0 | 78% | 75% | 83% | 2.43 | 2,9k | 228 s |
| Cosium/spring-data-jpa-entity-graph | `NamedEntityGraph` | c0 RUNTIME → VALID | 1 | 95% | 75% | 77% | 4.43 | 5,7k | 134 s |

- 4/4 class VALID; trung bình 0,25 vòng sửa, 4,1k token và 162 s mỗi class.
- Thời gian của class đầu mỗi repo có cả khối 1 (85 s và 120 s, gồm baseline ×2); class sau dùng chung khối 1 nên không tốn lại.
- jaqy có 2 test fail sẵn trong module: ARROW cảnh báo và không dùng chúng để kết luận regression.

## 4. Lỗi phát hiện

| # | Lỗi | Sửa | Test hồi quy |
|---|---|---|---|
| 1 | Cột "Ghi file" của bảng tổng in tên enum (`remoteproject`, `notvalid`) | Nhãn tiếng Việt (`chỉ trong runs/`, `đã ghi`, …); `--json` giữ nguyên | `GenCommandTextTests` |
| 2 | Chạy nhiều class: cảnh báo cuối không nói thuộc class nào (thấy ở E2E nhiều module) | Mỗi cảnh báo mở đầu bằng tên class | `ThreeClassesOfAMultiModuleProject` |

## 5. Còn lại

- Chạy 18 repo còn lại (chờ Chính đồng ý chi phí sau đợt thử).
- Ollama: chưa có trên máy.
- Rà `ErrorCatalog` và `doctor` (PLAN GĐ7 bước 5).
