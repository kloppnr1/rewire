# DST (Daylight Saving Time) Handling

How the time series extraction handles Denmark's DST transitions.

## Background

Denmark uses CET (Central European Time) in winter and CEST (Central European Summer Time) in summer:
- **CET**: UTC+1 (winter)
- **CEST**: UTC+2 (summer)

Two transitions each year:
1. **Spring Forward**: Last Sunday in March at 02:00 → clocks jump to 03:00 (1 hour disappears)
2. **Fall Back**: Last Sunday in October at 03:00 → clocks fall back to 02:00 (1 hour repeats)

---

## The Solution: Carry-Over Values

Instead of discarding invalid timestamp values OR creating duplicate UTC timestamps, we **add the invalid value to the next valid timestamp**. This preserves total energy consumption while maintaining unique UTC timestamps.

---

## Spring Forward Example (March 31, 2024)

### Source Data (CET/CEST)
```
01:00 CET  → 2.62 kWh  (becomes 00:00 UTC)
02:00 CET  → 1.33 kWh  ← INVALID (this time doesn't exist!)
03:00 CEST → 1.29 kWh  (becomes 01:00 UTC)
04:00 CEST → 1.66 kWh  (becomes 02:00 UTC)
```

### What Happens
1. System detects `02:00 CET` is invalid (doesn't exist during spring forward)
2. Stores the value (1.33 kWh) to carry over
3. Adds it to the next valid timestamp (`03:00 CEST`)

### Result (UTC)
```json
{
  "timeOfValue": "2024-03-31T00:00:00+00:00",
  "value": 1.390000000000,
  "qualityOfValue": "Maskinelt indlæst, godkendt"
},
{
  "timeOfValue": "2024-03-31T01:00:00+00:00",
  "value": 2.620000000000,
  "qualityOfValue": "Combined with invalid timestamp (1,29 + 1,33 kWh)"
},
{
  "timeOfValue": "2024-03-31T02:00:00+00:00",
  "value": 1.660000000000,
  "qualityOfValue": "Maskinelt indlæst, godkendt"
}
```

### Console Output
```
⚠ Invalid time in source: 2024-03-31 02:00:00 (doesn't exist in CET/CEST)
  - value 1,33 kWh will be added to next hour
→ Adding 1,33 kWh to 2024-03-31 03:00:00 → total: 2,62 kWh
```

---

## Fall Back Example (October 27, 2024)

### Source Data (CET/CEST)
```
01:00 CEST → 1.50 kWh  (becomes 23:00 UTC previous day)
02:00 ???  → 1.28 kWh  ← AMBIGUOUS (occurred twice!)
03:00 CET  → 1.30 kWh  (becomes 02:00 UTC)
```

The `02:00` timestamp is ambiguous:
- Could be `02:00 CEST` (first occurrence) = `00:00 UTC`
- Could be `02:00 CET` (second occurrence) = `01:00 UTC`

### What Happens
System consistently treats ambiguous times as **first occurrence** (CEST/summer time).

### Result (UTC)
```json
{
  "timeOfValue": "2024-10-27T00:00:00+00:00",
  "value": 1.280000000000,
  "qualityOfValue": "Ambiguous time - using CEST (first occurrence)"
}
```

### Console Output
```
⚠ Ambiguous time in source: 2024-10-27 02:00:00 → treating as CEST
  → 2024-10-27 00:00:00 UTC
```

---

## Key Benefits

- **No duplicate UTC timestamps** - All timestamps are unique
- **Energy is preserved** - Invalid values are carried over, not discarded
- **Clear marking** - Quality field shows combined values
- **Complete logging** - Console shows exactly what happened
- **Consistent handling** - Ambiguous times always use first occurrence

---

## Verification

### Duplicate Check
```bash
grep "timeOfValue" TimeSeries_*.json | sort | uniq -d | wc -l
# Result: 0 (no duplicates!)
```

### What Gets Logged
Every DST issue is logged to the console:
- Which invalid timestamps were found and where their values went
- Which ambiguous timestamps were interpreted and how
- The exact calculations for combined values
