---
id: generation.user.default
version: team-v1
role: user
used_by: zero_shot, few_shot
placeholders: cut_fqn, cut_path, test_package, test_class_name, cut_source
---
The class under test is {{cut_fqn}} (file {{cut_path}}).

Source code of the class:
<source>
{{cut_source}}
</source>

Write the test class for the class above.
- Package of the test class: {{test_package}}
- Test class name: {{test_class_name}}
- For the class under test, call only constructors and methods that appear in the source code above.
- Return Java code only, with no ``` and no explanations.
