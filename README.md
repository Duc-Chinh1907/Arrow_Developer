# ARROW Developer CLI

`arrow` là công cụ dòng lệnh giúp lập trình viên Java sinh unit test bằng LLM cho một class trong project Maven/Gradle:

1. phân tích project (thư mục trên máy hoặc repo GitHub);
2. sinh test bằng LLM;
3. xác minh test trên build thật, sửa tự động bằng **Adaptive Repair** khi test lỗi;
4. đo coverage (JaCoCo), mutation score (PIT) và test smell (tsDetect);
5. ghi `<CUT>_ArrowTest.java` vào project khi người dùng đồng ý, không bao giờ ghi đè file có sẵn.

Đây là bản sản phẩm của framework nghiên cứu ARROW (nhóm SET490_G2).

**Trạng thái:** đang phát triển, chưa có bản phát hành. Kế hoạch ở [docs/PLAN_CODE.vi.md](docs/PLAN_CODE.vi.md), kiến trúc ở [docs/ARCHITECTURE.vi.md](docs/ARCHITECTURE.vi.md), đặc tả ở [docs/specs/](docs/specs/).

## Build từ mã nguồn

Cần .NET SDK 10.0.404 trở lên (dải 10.0.4xx).

```bash
dotnet build ARROW.slnx
dotnet test --solution ARROW.slnx
```
