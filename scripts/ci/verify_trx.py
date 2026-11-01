"""Reject a green test command that produced no executed, passing test cases."""

from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def verify(root):
    summary = root.find(".//{*}ResultSummary")
    if summary is None or summary.get("outcome") != "Completed":
        raise ValueError("Test run did not complete successfully.")
    counters = summary.find("{*}Counters")
    if counters is None:
        raise ValueError("Test counters are missing.")
    total, executed, passed, failed = (int(counters.attrib[key]) for key in ("total", "executed", "passed", "failed"))
    results = root.findall(".//{*}UnitTestResult")
    if not (total > 0 and total == executed == passed == len(results) and failed == 0):
        raise ValueError("Tests must actually execute and all pass; skipped or empty runs are not validation.")
    if any(int(value) != 0 for key, value in counters.attrib.items() if key not in ("total", "executed", "passed")):
        raise ValueError("A non-passing test counter was reported.")
    if any(result.get("outcome") != "Passed" for result in results):
        raise ValueError("A test result did not pass.")
    return passed


def main(paths):
    if not paths:
        raise ValueError("At least one expected TRX file is required.")
    for path in paths:
        count = verify(ET.parse(Path(path)).getroot())
        print(f"Verified {count} executed passing test cases.")


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except (OSError, ET.ParseError, ValueError, KeyError):
        print("Test evidence is missing, malformed, empty, skipped or failing.", file=sys.stderr)
        sys.exit(1)
