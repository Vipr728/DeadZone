# Prompt contracts

`report.v1` is implemented in `playtest_lab.qwen`. It accepts only deterministic
evidence records and emits:

- `executive_summary`
- `top_findings`
- `recommendations`
- `limitations`

Every finding and recommendation must cite an evidence id. Synthetic evidence
cannot be described as Unity-verified, uploaded text is treated as untrusted
data, and impossibility language is allowed only for exhaustive proof results.
The service records the prompt version and SHA-256 digest with each successful
Qwen response.

