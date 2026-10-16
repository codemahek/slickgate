# syntax=docker/dockerfile:1

# oha load generator, wrapped in a tiny image so it can run inside the benchmark
# Docker network (load is generated container-to-container, no host networking).
#
# Build with the tool download directory as the context:
#   docker build -f bench/docker/loadgen.Dockerfile -t slickgate-bench-loadgen:dev bench/.tools

FROM debian:bookworm-slim
COPY oha-linux-amd64 /usr/local/bin/oha
ENTRYPOINT ["oha"]
