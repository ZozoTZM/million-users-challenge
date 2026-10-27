FROM node:22-bookworm-slim

ARG SUBPATH=submissions/csharp-aspnet-multiprocess-zozotzm

RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY seed ./seed
COPY schema.sql ./
COPY ${SUBPATH}/dev/seed/run.sh /run.sh
# The Windows checkout has CRLF; the benchmark checkout is LF. Normalize for local Docker.
RUN sed -i 's/\r$//' /app/seed/make-seed.sh /app/schema.sql /run.sh \
 && chmod +x /run.sh

ENTRYPOINT ["bash", "/run.sh"]
