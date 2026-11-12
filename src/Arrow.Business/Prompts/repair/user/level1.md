---
id: repair.user.level1
version: v2-draft1
role: user
used_by: fixed (every round), adaptive level 1
placeholders: failure_summary, test_package, test_class_name, repository_configuration, error_block, test_source, cut_fqn, cut_path, cut_source
---
The test class below failed ARROW's verification. {{failure_summary}}

Required test class
- Package: {{test_package}}
- Class name: {{test_class_name}}

Repository configuration (compatibility constraints):
{{repository_configuration}}

Failure output:
{{error_block}}

Current test class:
```java
{{test_source}}
```

Target CUT {{cut_fqn}} ({{cut_path}}):
```java
{{cut_source}}
```

Return only the complete corrected Java source of {{test_class_name}}, according to the output contract.
