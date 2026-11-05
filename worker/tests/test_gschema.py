"""G-schema (U1 D-H11), Python half.

The C# test (tests/Tasktor.Contracts.Tests/GSchemaTests.cs) proves every fixture is exactly what C# writes
for its record. Here every fixture must validate against the schema generated from those records, and the
invalid fixtures must not. Together: C# -> JSON -> Python validate -> C#, for every public contract.
"""
import json
import pathlib

import jsonschema
import pytest

REPO = pathlib.Path(__file__).resolve().parents[2]
SCHEMAS = REPO / "schemas"
FIXTURES = REPO / "tests" / "fixtures"


def _schema(stem):
    return json.loads((SCHEMAS / f"{stem}.json").read_text(encoding="utf-8"))


def _cases(root):
    out = []
    for schema_file in sorted(SCHEMAS.glob("*.json")):
        folder = root / schema_file.stem
        for fixture in sorted(folder.glob("*.json")) if folder.is_dir() else []:
            out.append(pytest.param(schema_file.stem, fixture, id=f"{schema_file.stem}/{fixture.name}"))
    return out


def test_every_schema_is_a_valid_2020_12_schema():
    for schema_file in SCHEMAS.glob("*.json"):
        jsonschema.Draft202012Validator.check_schema(json.loads(schema_file.read_text(encoding="utf-8")))


@pytest.mark.parametrize("stem,fixture", _cases(FIXTURES))
def test_fixture_validates(stem, fixture):
    jsonschema.Draft202012Validator(_schema(stem)).validate(json.loads(fixture.read_text(encoding="utf-8")))


@pytest.mark.parametrize("stem,fixture", _cases(FIXTURES / "invalid"))
def test_invalid_fixture_fails(stem, fixture):
    validator = jsonschema.Draft202012Validator(_schema(stem))
    assert list(validator.iter_errors(json.loads(fixture.read_text(encoding="utf-8")))), f"{fixture} should be invalid"


def test_every_schema_has_fixtures():
    for schema_file in SCHEMAS.glob("*.json"):
        assert any((FIXTURES / schema_file.stem).glob("*.json")), schema_file.name
