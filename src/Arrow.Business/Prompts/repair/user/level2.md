---
id: repair.user.level2
version: v2-draft2
role: user
used_by: adaptive level 2 (also used when level 3 does not apply)
placeholders: failure_summary, test_package, test_class_name, repository_configuration, error_block, extra_context, failed_attempts, test_source, cut_fqn, cut_path, cut_source
---
The test class below failed ARROW's verification. {{failure_summary}} Earlier repairs of this failure did not succeed, so additional information is provided.

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

Return only the complete corrected Java source of {{test_class_name}}, according to the output contract.
