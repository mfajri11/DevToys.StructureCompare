# Structured Compare for DevToys

A [DevToys 2.0](https://devtoys.app/) extension (under **Text**) to compare **YAML**, **JSON**, and **.env** files with semantic normalization.

## Features

- **Formats**: `Auto-Detect`, `YAML`, `JSON`, and `.env`
- **Order Sensitivity**:
  - **Strict Order**: Normalizes formatting and whitespace while preserving key and list item order.
  - **Ignore Order**: Deterministically sorts mapping/object keys, sequence/array items, and `.env` variables before diffing.
- **HighlightedHighlighted OutputOutput & Tooltips**: KeepsKeeps **Original** and **Modified** input editors clean, while highlighting differences in both **Split View** and **Inline View** outputs with hover explanations (`Red` = removed, `Green` = added, `Purple` = modified/conflict value, `Blue` = reordered under Strict Order).
- **Diff View**: Side-by-side highlighted **Split View** or unified highlighted **Inline View**.
- **Diagnostics**: Clear status banner showing difference counts, equivalence, or exact parse error line/column.

## Build, Test, and Package

```bash
dotnet test -c Release
dotnet pack src/StructuredCompare/StructuredCompare.csproj -c Release -o artifacts
```

Install the generated `.nupkg` from `artifacts/` in DevToys via **Manage extensions → Install an extension from file**.
