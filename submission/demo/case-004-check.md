## case-004 / DOC-004: stored state vs reference

- Extracted facts: 37/38 values equal to expected/extraction.json
  - /bank/iban: expected 'DE00SYNTHETIC0000000001', got 'DE00 SYNT HETI C000 0000 001'
- Evidence: 34/34 expected fields have a source; 23/34 with the identical page and excerpt
- Conclusion (assessment `case-004/DOC-004/r2`, input revision 2): decision `needs_review`, import `new` - matches expected/step-001.json under the runner's rules
