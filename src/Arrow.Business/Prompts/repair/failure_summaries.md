---
id: repair.failure_summaries
version: v2-draft1
role: data
used_by: placeholder failure_summary of repair/user/level1, level2, level3
---
COMPILE: It does not compile.
UNKNOWN: The build failed before the tests could run, and the compiler reported no error in the test file.
DISCOVERY: It compiles, but the test runner did not discover or run any of its test methods.
RUNTIME: Some of its test methods throw unexpected exceptions while running.
ASSERTION: Its test methods run, but some assertions fail.
REGRESSION: Its test methods pass when run alone, but running it together with the module's existing tests makes existing tests fail or stop running, or makes this test fail.
