# stop-at-second

Fixture `maven-verify-junit4`, `PriceCalculator` then `Invoice` with `--jobs 1`: the first class is VALID, the call of the
second answers HTTP 401 (`B2_LLM_CONFIG`), so P03 stops the run (P07 §8). Resuming it with `two-classes` keeps `PriceCalculator`
and runs `Invoice` again (P07 §5.3).
