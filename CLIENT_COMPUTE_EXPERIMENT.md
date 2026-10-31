# Client-compute Web experiment

This branch packages the browser UI as a same-origin directory. The manual GitHub Actions workflow builds both the small metadata API server and a static `roller-seal/` folder ready to extract into the existing `iobyte.cn` website document root. Extraction creates `/roller-seal/` and leaves existing homepage files untouched.

The page hosts the official SolidCraft WebAssembly release beneath `/roller-seal/solidcraft/`; the table, CSV parsing, validation, model updates, STEP exports, optional assembly/interference checks, and ZIP packaging run in the browser. Generated model files are read from the browser's OPFS and packaged into a ZIP locally. The ZIP is downloaded by the browser.

After a batch finishes, the page sends one record containing normalized parameter rows and the browser-reported generation timestamp to `https://srs.arcsiny.com/api/generation-records`. The server derives the client IP from the request connection, using the rightmost `X-Forwarded-For` address only when the peer is loopback (the local reverse proxy). Records are appended to `WebData/browser-generation-records.jsonl`. Generated CAD files are never uploaded. The API accepts requests from `https://iobyte.cn` and `https://www.iobyte.cn`; other API routes return 404 on this branch.

The static site archive and API server artifact are separate. Publishing the subdirectory does not deploy it. The API package must be deployed to `srs.arcsiny.com` before the page can save generation records.

This experiment currently supports SolidCraft designs, STEP, combined STEP, and CSV output. Browser-side DXF and PDF generation are not implemented yet; the page does not send those jobs to the server.
