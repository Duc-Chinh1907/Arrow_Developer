# o8-o3-o4-o7-o1

The chain of R03 §14.5 on fixture `maven-verify-junit4`, CUT `PriceCalculator` (P11 §3.3).

| Round | Answer | Verification | Decision |
|---|---|---|---|
| c0 | `sum(2, 3)` does not exist | `COMPILE \| cannot find symbol \| method` | checkpoint c0, level 1 |
| 1 (O8) | `new PriceCalculator(1)` | `COMPILE \| constructor cannot be applied \| -` | EQUAL, other signature: keep level 1 |
| 2 (O3) | same code as round 1 | not verified | duplicate code: level 2 |
| 3 (O4) | `assertEquals(6, add(2, 3))` | `ASSERTION \| AssertionError \| adds`, 1/2 pass | BETTER: checkpoint, level 1 |
| 4 (O7) | `assertEquals(7, add(3, 3))` | same signature, 1/2 pass | EQUAL, repeated signature: level 2 |
| 5 (O1) | correct test | VALID | success in round 5 |

Every answer has the same two assertions, so the comparisons depend only on the state and the tests passed.
