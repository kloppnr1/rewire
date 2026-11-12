"""Generate the mapped Xellent column reference from the two EF entity models.

Run from this repository with: python tools/generate-xellent-schema-reference.py
No database connection is used. The file is a code-model inventory, not a claim
that the mapped types cover every table or column in SQL Server.
"""

from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
WORKSPACE = ROOT.parent
SOURCES = (
    ("XellentDomain", ROOT / "src/DataMigration.XellentData"),
    ("CustomerSupport", WORKSPACE / "CustomerSupport/src/CustomerSupport.XellentDatabaseModel"),
)
SCALAR = {"bool", "byte", "short", "int", "long", "float", "double", "decimal", "string", "DateTime", "DateOnly", "TimeOnly", "Guid", "byte[]"}


def model(path: Path):
    text = path.read_text(encoding="utf-8-sig")
    fluent = {}
    for name, expression in re.findall(
        r"modelBuilder\.Entity<(\w+)>\(\)\s*\.HasKey\(\w+ => (.*?)\);",
        text,
        re.S,
    ):
        fluent[name] = re.findall(r"\.([A-Za-z]\w*)", expression)
    return fluent


def parse_entity(path: Path, fluent: dict):
    text = path.read_text(encoding="utf-8-sig")
    table = re.search(r'\[Table\("([^"]+)"', text)
    klass = re.search(r"public\s+(?:partial\s+)?class\s+(\w+)", text)
    if not table or not klass:
        return None
    class_name = klass.group(1)
    declared_key = re.search(r"\[PrimaryKey\((.*?)\)\]", text, re.S)
    key = fluent.get(class_name)
    key_source = "Fluent HasKey"
    if key is None and "[Keyless]" in text:
        key, key_source = [], "Keyless"
    if key is None and declared_key:
        key, key_source = re.findall(r"nameof\((\w+)\)", declared_key.group(1)), "PrimaryKey attribute"
    columns = {}
    attributes = []
    for line in text.splitlines():
        stripped = line.strip()
        match = re.search(r"\bpublic\s+([\w\[\]]+\??)\s+(\w+)\s*\{\s*get;\s*set;", stripped)
        if match:
            clr, property_name = match.groups()
            if clr.removesuffix("?") in SCALAR:
                attr_text = " ".join(attributes + [stripped[:match.start()]])
                col_attr = re.search(r'\[Column\("([^"]+)"(?:,\s*TypeName\s*=\s*"([^"]+)")?\)\]', attr_text)
                length = re.search(r"\[StringLength\((\d+)\)\]", attr_text)
                sql_name = col_attr.group(1) if col_attr else property_name.upper()
                sql_type = clr
                if col_attr and col_attr.group(2):
                    sql_type += f"; SQL {col_attr.group(2)}"
                if length:
                    sql_type += f" (max {length.group(1)})"
                columns[sql_name] = (property_name, sql_type, bool(col_attr), "[Key]" in attr_text)
            attributes = []
        elif stripped.startswith("["):
            attributes.append(stripped)
        elif stripped and not stripped.startswith("///") and not stripped.startswith("//"):
            attributes = []
    if key is None:
        key = [name for name, (_, _, _, marked) in columns.items() if marked]
        key = [columns[name][0] for name in key]
        key_source = "Key attribute" if key else "No explicit key in entity/context"
    mapped_key = [next((column for column, (prop, *_rest) in columns.items() if prop == member), member.upper()) for member in key]
    return table.group(1), class_name, columns, mapped_key, key_source


def main():
    tables = {}
    for label, directory in SOURCES:
        entities_dir = directory / "Entities"
        if not entities_dir.exists():
            raise SystemExit(f"Missing model source: {entities_dir}")
        fluent = model(next(directory.glob("*Context.cs")))
        for path in sorted(entities_dir.glob("*.cs")):
            parsed = parse_entity(path, fluent)
            if not parsed:
                continue
            table, class_name, columns, key, key_source = parsed
            entry = tables.setdefault(table, {})
            entry[label] = (class_name, columns, key, key_source, path)

    xellent_count = sum("XellentDomain" in entry for entry in tables.values())
    support_only = sum("XellentDomain" not in entry for entry in tables.values())
    lines = [
        "# Xellent EF schema reference",
        "",
        "Generated from C# entity classes and `DbContext` configuration by",
        "`python tools/generate-xellent-schema-reference.py`. No database queries are made.",
        f"Coverage: **{len(tables)} distinct mapped tables**: {xellent_count} in XellentDomain's",
        f"`XellentDbContext` and {support_only} additional tables in CustomerSupport's",
        "`XellentDatabaseModelContext`. This is the union of those code models, not an inventory",
        "of the entire SQL Server database. See [the model guide](xellent-model-guide.md) for",
        "relationships, scope, and EF investigation examples.",
        "",
        "`X` = mapped by XellentDomain; `CS` = mapped by CustomerSupport. Column names",
        "without `[Column]` are derived by EF convention and marked `convention`.",
        "The listed key is the effective EF model key, which may differ from `[Key]` attributes",
        "and is not proof of a SQL Server primary key. Types describe C# mappings, with an",
        "explicit SQL type or string length where the class provides one.",
        "A `?` records C# nullable syntax; it is not a live check of database nullability.",
        "Type facets configured only through fluent calls (for example HasPrecision) are",
        "not extracted here. See [field semantics](xellent-field-semantics.md) for those",
        "differences and the important codes, units, dates, and history rules.",
        "",
        "| SQL table | EF models | Columns in union |",
        "|---|---|---:|",
    ]
    for table, entry in sorted(tables.items()):
        union = set().union(*(v[1] for v in entry.values()))
        models = ", ".join("X" if m == "XellentDomain" else "CS" for m in entry)
        lines.append(f"| [`{table}`](#{table.lower()}) | {models} | {len(union)} |")

    for table, entry in sorted(tables.items()):
        lines.extend(["", f"## {table}", ""])
        for label, (class_name, columns, key, key_source, path) in entry.items():
            relative = Path("..") / path.relative_to(ROOT) if path.is_relative_to(ROOT) else Path("../../CustomerSupport") / path.relative_to(WORKSPACE / "CustomerSupport")
            prefix = "X" if label == "XellentDomain" else "CS"
            lines.append(f"- **{prefix}:** [`{class_name}`]({relative.as_posix()}) — EF key: " + (", ".join(f"`{c}`" for c in key) if key else "keyless / unspecified") + f" ({key_source}).")
        lines.extend(["", "| SQL column | C# property / model | Type and mapping |", "|---|---|---|"])
        union = sorted(set().union(*(v[1] for v in entry.values())))
        for column in union:
            owners = []
            types = []
            for label, (_class_name, columns, _key, _key_source, _path) in entry.items():
                if column not in columns:
                    continue
                prop, typ, explicit, _marked = columns[column]
                abbreviation = "X" if label == "XellentDomain" else "CS"
                owners.append(f"{abbreviation}: `{prop}`")
                types.append(f"{abbreviation}: `{typ}`" + ("" if explicit else " (convention)"))
            lines.append(f"| `{column}` | {'; '.join(owners)} | {'; '.join(types)} |")
    output = ROOT / "docs/xellent-schema-reference.md"
    output.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {output}: {len(tables)} tables")


if __name__ == "__main__":
    main()
