"""Explicit test-only waiver for a hosted runner with no standard-user token.

This module is never part of the installed application or its updater.
"""
import os
from pathlib import Path


class UnsupportedStandardUserRunner(RuntimeError):
    """The unmodified SAFER token remained elevated/above medium integrity."""


def record_unsupported_runner_skip(error, arguments, environment=None):
    """Only the opted-in, known hosted-runner limitation may become a skip."""
    environment = os.environ if environment is None else environment
    if (not isinstance(error, UnsupportedStandardUserRunner)
            or "--allow-unsupported-ci-runner" not in arguments
            or environment.get("GITHUB_ACTIONS") != "true"
            or environment.get("RUNNER_ENVIRONMENT") != "github-hosted"
            or environment.get("RUNNER_OS") != "Windows"):
        return False
    summary = ("## Windows standard-user installer runtime test: SKIPPED\n\n"
               "The GitHub-hosted Windows runner did not provide a genuine unelevated "
               "standard-user token. This known runner limitation is explicitly waived for CI.\n\n"
               "- Per-user installation/reinstallation/uninstallation was **not verified**\n"
               "- Elevated normal/stage/register setup refusal assertions passed before this skip\n"
               "- Installer build, structural checks, native payload checks and GUI checks remain required\n"
               "- No production installer/updater guard, account, credential or security policy was changed\n\n"
               f"Token diagnostic: {error}\n\n")
    print(summary, flush=True)
    if summary_path := environment.get("GITHUB_STEP_SUMMARY"):
        with Path(summary_path).open("a", encoding="utf-8") as output:
            output.write(summary)
    return True
