---
id: repair.user.level3
version: v2-draft2
role: user
used_by: adaptive level 3 (only for COMPILE, RUNTIME, ASSERTION when the failing methods are known)
placeholders: failure_summary, failing_methods, test_package, test_class_name, repository_configuration, error_block, extra_context, failed_attempts, test_source, cut_fqn, cut_path, cut_source
---
The test class below failed ARROW's verification. {{failure_summary}} The failure is located in these test methods: {{failing_methods}}.

Rewrite only these test methods. Keep every other method, field, and import of the current test class unchanged. You may add imports, fields, or helper methods that the rewritten methods need. ARROW will take only the listed methods, plus any new imports, fields, and helper methods, from your answer and put them into the current test class.

Required test class
- Package: {{test_package}}
- Class name: {{test_class_name}}

Repository configuration (compatibility constraints):
{{repository_configuration}}

Failure output:
{{error_block}}

Additional information for this failure:
{{extra_context}}

Approaches that already failed (do not repeat them; each entry shows the round, the failure signature, the first error, and the code that was changed):
{{failed_attempts}}

Current test class:
```java
{{test_source}}
```

Target CUT {{cut_fqn}} ({{cut_path}}):
```java
{{cut_source}}
```

Return the complete Java source of {{test_class_name}} with the listed methods rewritten, according to the output contract.
