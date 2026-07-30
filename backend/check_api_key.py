"""
Standalone "Test Key" CLI for the add-in's AI Vision Validation section - a
fast, raster-free round trip to confirm an API key/model pair actually works
before running a full detection. Called by
TreeCounterAddin/PythonBackendService.cs.TestApiKeyAsync.

Named check_api_key.py (not test_api_key.py) so deploy.ps1's packaging filter
(which excludes pytest-style test_*.py files) still ships it.
"""
import argparse
import sys

from validator import test_api_key


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--provider", required=True, choices=["gemini", "openai", "claude"])
    parser.add_argument("--api-key", required=True)
    parser.add_argument("--model", required=True)
    args = parser.parse_args()

    ok, message = test_api_key(args.provider, args.api_key, args.model)
    print(message)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
