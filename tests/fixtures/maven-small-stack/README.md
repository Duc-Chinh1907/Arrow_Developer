Fixture: Surefire chạy test với `-Xss256k` (như jsoup). JDK 8 bản Zulu trên Mac ARM đòi stack ít nhất 640k nên JVM chạy test
không khởi động được; khối 1 phải biên dịch lại bằng JDK kế tiếp (spec 01 Mục 4.3 bước 5). Trên máy mà JDK 8 chạy được thì không đổi JDK.
