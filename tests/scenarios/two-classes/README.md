# two-classes

Fixture `maven-verify-junit4`, two CUTs of the same module, `PriceCalculator` and `Invoice` (P07 §7): both answers are VALID at
c0, and block 1 runs once for the module. `Invoice_ArrowTest` does not change `Invoice.defaultRate`, so `TotalsTest` still passes.
