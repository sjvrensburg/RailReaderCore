#!/usr/bin/env bash
# Rebuild the rotation fixture PDFs from their LaTeX sources.
# The generated PDFs are committed so CI and tests do not need LaTeX.
set -euo pipefail
cd "$(dirname "$0")"
for tex in rotate-suite sideways-table landscape-scan; do
    pdflatex -interaction=nonstopmode -halt-on-error "$tex.tex" >/dev/null
    rm -f "$tex.aux" "$tex.log" "$tex.out"
done
echo "Fixtures rebuilt."
