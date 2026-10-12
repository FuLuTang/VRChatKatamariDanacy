# P22 third-batch data checker

Run from the repository root:

```sh
python3 Docs/tools/third_batch/validate.py
python3 -m unittest discover -s Docs/tests/third_batch -v
```

The standard-library CLI performs a **focused** check of current P09–P18 machine-readable data: work-package and release DAG links, selected IDs, P10/P11 source and instance references, P12/P13 counterexample fixture presence and finite JSON numbers, P15 transition links and paid-store-off state, P15/P18 `NOT_RUN` fields, and P16's seven repository file byte/hash records and observation boundaries. P16's separate Windows installation is not inspected.

It is not a complete JSON Schema validator, a legal/license decision, an asset approval, a runtime or Unity test, performance evidence, or publication authorization. The check does not scan prose or test strings for secret-like patterns; those are not reliable leak detectors and would mislabel examples. Source/rights evidence and public push authorization remain human review tasks.
