"""Convenience dispatcher for `python -m playtester_infra`."""

from __future__ import annotations

import argparse

from playtester_infra.cli import egress_main, report_main, watch_main


def main() -> int:
    parser = argparse.ArgumentParser(prog="python -m playtester_infra")
    parser.add_argument("command", choices=("report", "watch", "egress-proof"))
    args, remainder = parser.parse_known_args()
    if args.command == "report":
        return report_main(remainder)
    if args.command == "watch":
        return watch_main(remainder)
    return egress_main(remainder)


if __name__ == "__main__":
    raise SystemExit(main())
