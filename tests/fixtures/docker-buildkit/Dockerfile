# Syntax that only BuildKit builds: the classic builder refuses `RUN --mount`, and reads
# `--parents` and `**` as neither.
FROM alpine:3
RUN --mount=type=bind,target=/ctx test -f /ctx/sub/dir/keep.toml
COPY --parents **/keep.toml /x/
