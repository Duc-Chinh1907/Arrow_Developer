# multi-module

Fixture `maven-multi-junit5`, three CUTs with `--jobs 2` (PLAN GĐ6):

- `OrderService` (module `shop-core`, JUnit 5): VALID at c0.
- `TaxPolicy` (module `shop-api`): the module declares no test framework, so block 1 warns (P01 §4.5) and the JUnit 5 answer
  does not compile. Every repair round gives the same answer, so the run stops on duplicates without INFRA, and nothing is written.
- `Greeter` (`samples/standalone`, outside the root reactor, JUnit 4): its own build root; VALID at c0.

Each CUT has its own module, so each has its own block 1 and baseline.
