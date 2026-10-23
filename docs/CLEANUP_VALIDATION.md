# Source cleanup validation — 23 October 2026

The staged source tree was exported with `git checkout-index` into a fresh
temporary directory, with no recovery directories, proprietary SDKs or generated
players. `uv run --locked script/check_source.py` passed there:

- 189 regression tests: 105 script/publication tests, 39 scene/bundle gate tests,
  29 runner-supervisor tests and 16 shader repair tests.
- Ruff 0.17.2 formatting and correctness lint; Python parsing and project XML parsing.
- clang-format 23.1.3 verification of native and C# sources.
- shfmt 3.14.1 formatting, ShellCheck 0.11.0 warning-level checks and Bash syntax checks.

In the working checkout, the managed-extension checker and native patch tool
built successfully with zero warnings/errors. The editor-cleanup fixture passed
12 baseline and 12 patched cases. The existing native plugin integration tests
also passed against local generated libraries before being separated from the
source-only test discovery.

The publication checker examined staged blobs against the explicit manifest.
No game asset or binary extensions were included; credential-pattern scanning
found no matches. The seven publication regression tests cover unreviewed files,
asset types, binary content, symlinks, missing files and staged-versus-working-copy
content. Existing remote history contained only the original license commit.

A Pillow `getdata()` deprecation warning remains; it does not fail the suite.
These results establish source/test portability, not a clean full-game rebuild,
physical headset acceptance or complete bootstrap reproducibility.
