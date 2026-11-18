---
id: generation.user.default
version: v2-draft1
role: user
used_by: zero_shot, few_shot
placeholders: cut_fqn, cut_path, test_package, test_class_name, cut_source
---
Generate a unit test class for the target class under test (CUT) below.

Target CUT
- Fully qualified name: {{cut_fqn}}
- Source file: {{cut_path}}

Required test class
- Package: {{test_package}}
- Class name: {{test_class_name}}

Target CUT source:
```java
{{cut_source}}
```

Return only the complete Java source of the test class {{test_class_name}}, according to the output contract.
