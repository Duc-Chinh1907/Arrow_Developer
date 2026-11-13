Fixture: một module Maven, Java 11, JUnit 5, **có Maven Wrapper** (`mvnw`, `mvnw.cmd`, `.mvn/wrapper/`). Khối 1 phải chạy
bằng wrapper chứ không bằng Maven của máy (P01 §4.2, P11 §2).

| Mục | Giá trị |
|---|---|
| CUT gợi ý | `com.acme.geo.Distance` |
| Baseline mong đợi | 1 test pass (`DistanceTest`), 0 fail, 0 flaky |
| Build tool | Maven **3.9.9** qua wrapper 3.3.2 (`only-script`): khác bản Maven của máy, nên `build_tool.version = 3.9.9` chứng tỏ wrapper được dùng. Lần chạy đầu wrapper tải bản phân phối về `~/.m2/wrapper/` |
| `generated_test_path` mong đợi | `src/test/java/com/acme/geo/Distance_ArrowTest.java` |
| Bộ test mẫu VALID | `.arrow-sample/Distance_ArrowTest.java`, 5 test |
| Chỉ số khối 4 với bộ mẫu | branch 100%; line gần 100% (chỉ constructor `private` không chạy); số chính xác và mutation đo, ghi lại ở GĐ5 |

`mvnw`, `mvnw.cmd` và `.mvn/wrapper/maven-wrapper.properties` được sinh ngày 13/11/2026 bằng
`mvn -N org.apache.maven.plugins:maven-wrapper-plugin:3.3.2:wrapper -Dtype=only-script -Dmaven=3.9.9` (Apache License 2.0).
