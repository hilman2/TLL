#!/bin/sh
# Installs the pinned checkers, then lints, type-checks and tests
# tools/metrics. The arguments go to pytest.
set -eu

pip install --quiet --user pytest==8.3.3 pytest-timeout==2.3.1 mypy==1.13.0 ruff==0.7.4
export PATH="$HOME/.local/bin:$PATH"

ruff check .
ruff format --check .
mypy
pytest "$@"
